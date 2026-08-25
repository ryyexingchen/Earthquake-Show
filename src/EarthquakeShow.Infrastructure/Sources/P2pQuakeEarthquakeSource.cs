using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class P2pQuakeEarthquakeSource : IRealtimeEarthquakeSource
{
    public const string DefaultEndpoint = "https://api.p2pquake.net/v2/jma/quake";
    private const string SourceName = "p2pquake";
    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);
    private static readonly string[] SourceTimeFormats =
    [
        "yyyy/MM/dd HH:mm:ss.FFFFFFF",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex MunicipalityPattern = new(
        "^(?<name>.+?(?:市|区|町|村))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly JmaIntensityRegionCatalog? _regionCatalog;
    private readonly JmaStationCoordinateCatalog? _stationCatalog;

    public P2pQuakeEarthquakeSource(
        HttpClient httpClient,
        string endpoint = DefaultEndpoint,
        JmaIntensityRegionCatalog? regionCatalog = null,
        JmaStationCoordinateCatalog? stationCatalog = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("P2PQuake 地址必须是 HTTP 或 HTTPS URL。", nameof(endpoint));
        }

        _endpoint = endpointUri;
        _regionCatalog = regionCatalog;
        _stationCatalog = stationCatalog;
    }

    public string SourceId => SourceName;

    public async Task<EarthquakeSourceFetchResult> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new EarthquakeSourceFetchResult(
                    [],
                    new SourceStatus(
                        SourceId,
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? SourceConnectionState.RateLimited
                            : SourceConnectionState.Disconnected,
                        checkedAt,
                        Detail: $"P2PQuake HTTP {(int)response.StatusCode}"));
            }

            string payload = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            ImmutableArray<EarthquakeReport> reports = ParseReports(
                payload,
                checkedAt,
                _endpoint,
                _regionCatalog,
                _stationCatalog);
            DateTimeOffset? latestReceivedAt = reports.IsDefaultOrEmpty
                ? null
                : reports.Max(report => (DateTimeOffset?)report.ReceivedAt);
            return new EarthquakeSourceFetchResult(
                reports,
                new SourceStatus(
                    SourceId,
                    SourceConnectionState.Online,
                    checkedAt,
                    latestReceivedAt,
                    $"P2PQuake：{reports.Length} 条"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"P2PQuake JSON 格式错误：{exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(SourceConnectionState.Disconnected, checkedAt, $"P2PQuake 网络错误：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"P2PQuake 字段错误：{exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"P2PQuake 字段越界：{exception.Message}");
        }
        catch (IOException exception)
        {
            return Failure(SourceConnectionState.Disconnected, checkedAt, $"P2PQuake 读取错误：{exception.Message}");
        }
    }

    internal static ImmutableArray<EarthquakeReport> ParseReports(
        string payload,
        DateTimeOffset receivedAt,
        Uri endpoint,
        JmaIntensityRegionCatalog? regionCatalog = null,
        JmaStationCoordinateCatalog? stationCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("P2PQuake 顶层必须是数组。");
        }

        var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            reports.Add(ParseReport(element, receivedAt, endpoint, regionCatalog, stationCatalog));
        }

        return reports.ToImmutable();
    }

    internal static EarthquakeReport ParseReport(
        JsonElement element,
        DateTimeOffset receivedAt,
        Uri endpoint,
        JmaIntensityRegionCatalog? regionCatalog = null,
        JmaStationCoordinateCatalog? stationCatalog = null)
    {
        P2pQuakeItem item = element.Deserialize<P2pQuakeItem>(JsonOptions)
            ?? throw new JsonException("P2PQuake 报文不能为空。");
        return ToReport(item, element.GetRawText(), receivedAt, endpoint, regionCatalog, stationCatalog);
    }

    private static EarthquakeReport ToReport(
        P2pQuakeItem item,
        string rawPayload,
        DateTimeOffset receivedAt,
        Uri endpoint,
        JmaIntensityRegionCatalog? regionCatalog,
        JmaStationCoordinateCatalog? stationCatalog)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new FormatException("P2PQuake 报文缺少 id。");
        }

        if (item.Issue is null || item.Earthquake is null || item.Earthquake.Hypocenter is null)
        {
            throw new FormatException("P2PQuake 报文缺少 issue 或 earthquake.hypocenter。");
        }

        DateTimeOffset issuedAt = ParseRequiredTime(item.Issue.Time, "issue.time");
        DateTimeOffset reportReceivedAt = ParseOptionalTime(item.Time) ?? receivedAt;
        P2pHypocenter sourceHypocenter = item.Earthquake.Hypocenter;
        GeoCoordinate? coordinate = TryCreateCoordinate(
            sourceHypocenter.Latitude,
            sourceHypocenter.Longitude);
        int? depthKm = sourceHypocenter.Depth is >= 0
            ? sourceHypocenter.Depth
            : null;
        string? hypocenterName = NullIfUnknown(sourceHypocenter.Name);
        Hypocenter? hypocenter = coordinate is null && depthKm is null && hypocenterName is null
            ? null
            : new Hypocenter(hypocenterName, null, coordinate, depthKm);
        Magnitude? magnitude = sourceHypocenter.Magnitude is double magnitudeValue && magnitudeValue >= 0
            ? new Magnitude(magnitudeValue)
            : null;
        JmaIntensity maxIntensity = ParseIntensity(item.Earthquake.MaxScale);
        (ImmutableArray<IntensityArea> areas,
            ImmutableArray<IntensityMunicipality> municipalities,
            ImmutableArray<IntensityStation> stations) = ParseObservations(
                item.Points,
                regionCatalog,
                stationCatalog);

        return new EarthquakeReport
        {
            EventId = $"p2pquake:{item.Id}",
            ReportCode = $"P2P-{item.Code}",
            ReportType = ParseReportType(item.Issue.Type),
            DistantEarthquakeKind = ParseDistantEarthquakeKind(
                item.Issue.Type,
                item.Comments?.FreeFormComment),
            Status = ParseReportStatus(item.Issue.Correct),
            Context = ReportContext.Normal,
            OriginTime = ParseOptionalTime(item.Earthquake.Time),
            IssuedAt = issuedAt,
            ReceivedAt = reportReceivedAt,
            Hypocenter = hypocenter,
            Magnitude = magnitude,
            MaxIntensity = maxIntensity,
            IntensityAreas = areas,
            IntensityMunicipalities = municipalities,
            IntensityStations = stations,
            TsunamiComment = BuildTsunamiComment(
                item.Earthquake.DomesticTsunami,
                item.Earthquake.ForeignTsunami),
            Source = new SourceReference(
                SourceName,
                item.Id,
                endpoint,
                rawPayload),
        };
    }

    private static (
        ImmutableArray<IntensityArea> Areas,
        ImmutableArray<IntensityMunicipality> Municipalities,
        ImmutableArray<IntensityStation> Stations) ParseObservations(
        P2pPoint[]? points,
        JmaIntensityRegionCatalog? regionCatalog,
        JmaStationCoordinateCatalog? stationCatalog)
    {
        if (points is null or { Length: 0 })
        {
            return ([], [], []);
        }

        var areas = new Dictionary<string, (string Name, string PrefectureCode, string PrefectureName, JmaIntensity Intensity)>(StringComparer.Ordinal);
        var municipalities = new Dictionary<string, (string Name, string AreaCode, JmaIntensity Intensity)>(StringComparer.Ordinal);
        var stations = ImmutableArray.CreateBuilder<IntensityStation>();
        foreach (P2pPoint point in points)
        {
            if (string.IsNullOrWhiteSpace(point.Addr))
            {
                continue;
            }

            string prefecture = NullIfUnknown(point.Prefecture) ?? "unknown";
            string address = point.Addr.Trim();
            JmaIntensity intensity = ParseIntensity(point.Scale);
            if (point.IsArea)
            {
                JmaIntensityAreaDefinition resolvedArea = null!;
                bool areaResolved = regionCatalog is not null &&
                    regionCatalog.TryResolveAreaName(address, out resolvedArea);
                string resolvedAreaCode = areaResolved
                    ? resolvedArea.Code
                    : $"p2p-area:{prefecture}:{address}";
                string resolvedPrefectureCode = areaResolved
                    ? resolvedArea.PrefectureCode
                    : $"p2p-pref:{prefecture}";
                string resolvedPrefectureName = areaResolved
                    ? resolvedArea.PrefectureName
                    : prefecture;
                string resolvedAreaName = areaResolved ? resolvedArea.Name : address;
                areas[resolvedAreaCode] = UpdateIntensity(
                    areas.TryGetValue(resolvedAreaCode, out (string Name, string PrefectureCode, string PrefectureName, JmaIntensity Intensity) existingArea)
                        ? existingArea
                        : (resolvedAreaName, resolvedPrefectureCode, resolvedPrefectureName, JmaIntensity.Unknown),
                    intensity);
                continue;
            }

            JmaIntensityMunicipalityDefinition? municipalityDefinition = null;
            JmaIntensityMunicipalityDefinition resolvedDefinition = null!;
            bool resolved = regionCatalog is not null &&
                regionCatalog.TryResolveMunicipality(
                    prefecture,
                    address,
                    out resolvedDefinition);
            if (resolved)
            {
                municipalityDefinition = resolvedDefinition;
            }
            string municipalityName = resolved
                ? municipalityDefinition!.Name
                : ParseMunicipalityName(address, prefecture);
            string areaCode = resolved ? municipalityDefinition!.AreaCode : $"p2p-area:{prefecture}";
            string municipalityCode = resolved
                ? municipalityDefinition!.Code
                : $"p2p-municipality:{prefecture}:{municipalityName}";
            string prefectureCode = resolved
                ? municipalityDefinition!.PrefectureCode
                : $"p2p-pref:{prefecture}";
            string prefectureName = resolved ? municipalityDefinition!.PrefectureName : prefecture;
            string areaName = resolved ? municipalityDefinition!.AreaName : prefecture;
            GeoCoordinate? coordinate = ResolveStationCoordinate(
                stationCatalog,
                address,
                prefecture);
            areas[areaCode] = UpdateIntensity(
                areas.TryGetValue(areaCode, out (string Name, string PrefectureCode, string PrefectureName, JmaIntensity Intensity) area)
                    ? area
                    : (areaName, prefectureCode, prefectureName, JmaIntensity.Unknown),
                intensity);
            municipalities[municipalityCode] = UpdateMunicipalityIntensity(
                municipalities.TryGetValue(municipalityCode, out (string Name, string AreaCode, JmaIntensity Intensity) municipality)
                    ? municipality
                    : (municipalityName, areaCode, JmaIntensity.Unknown),
                intensity);
            string key = $"p2p:{prefecture}:{address}";
            stations.Add(new IntensityStation(
                key,
                address,
                municipalityCode,
                intensity,
                coordinate));
        }

        return (
            areas.Select(item => new IntensityArea(
                    item.Key,
                    item.Value.Name,
                    item.Value.PrefectureCode,
                    item.Value.PrefectureName,
                    item.Value.Intensity))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToImmutableArray(),
            municipalities.Select(item => new IntensityMunicipality(
                    item.Key,
                    item.Value.Name,
                    item.Value.AreaCode,
                    item.Value.Intensity))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToImmutableArray(),
            stations.ToImmutable());
    }

    private static string ParseMunicipalityName(string address, string prefecture)
    {
        string withoutPrefecture = address.StartsWith(prefecture, StringComparison.Ordinal)
            ? address[prefecture.Length..]
            : address;
        Match match = MunicipalityPattern.Match(withoutPrefecture);
        return match.Success ? match.Groups["name"].Value : "unknown";
    }

    private static GeoCoordinate? ResolveStationCoordinate(
        JmaStationCoordinateCatalog? stationCatalog,
        string address,
        string prefecture)
    {
        if (stationCatalog is null)
        {
            return null;
        }

        string stationName = address.StartsWith(prefecture, StringComparison.Ordinal)
            ? address[prefecture.Length..]
            : address;
        return stationCatalog.TryResolve(null, stationName, out GeoCoordinate coordinate, out _)
            ? coordinate
            : stationCatalog.TryResolve(null, address, out coordinate, out _)
                ? coordinate
                : null;
    }

    private static (string Name, string PrefectureCode, string PrefectureName, JmaIntensity Intensity) UpdateIntensity(
        (string Name, string PrefectureCode, string PrefectureName, JmaIntensity Intensity) current,
        JmaIntensity value)
    {
        return (current.Name, current.PrefectureCode, current.PrefectureName, MaxIntensity(current.Intensity, value));
    }

    private static (string Name, string AreaCode, JmaIntensity Intensity) UpdateMunicipalityIntensity(
        (string Name, string AreaCode, JmaIntensity Intensity) current,
        JmaIntensity value)
    {
        return (current.Name, current.AreaCode, MaxIntensity(current.Intensity, value));
    }

    private static JmaIntensity MaxIntensity(JmaIntensity left, JmaIntensity right)
    {
        return left == JmaIntensity.Unknown ? right :
            right == JmaIntensity.Unknown ? left :
            (JmaIntensity)Math.Max((int)left, (int)right);
    }

    private static JmaIntensity ParseIntensity(int scale)
    {
        return scale switch
        {
            10 => JmaIntensity.One,
            20 => JmaIntensity.Two,
            30 => JmaIntensity.Three,
            40 => JmaIntensity.Four,
            45 => JmaIntensity.FiveLower,
            50 => JmaIntensity.FiveUpper,
            55 => JmaIntensity.SixLower,
            60 => JmaIntensity.SixUpper,
            70 => JmaIntensity.Seven,
            _ => JmaIntensity.Unknown,
        };
    }

    private static EarthquakeReportType ParseReportType(string? value)
    {
        return value switch
        {
            "DetailScale" => EarthquakeReportType.HypocenterAndIntensity,
            "ScalePrompt" => EarthquakeReportType.SeismicIntensity,
            "Destination" => EarthquakeReportType.Hypocenter,
            "Foreign" => EarthquakeReportType.DistantEarthquake,
            _ => EarthquakeReportType.Unknown,
        };
    }

    private static DistantEarthquakeKind? ParseDistantEarthquakeKind(
        string? issueType,
        string? freeFormComment)
    {
        if (!string.Equals(issueType, "Foreign", StringComparison.Ordinal))
        {
            return null;
        }

        return freeFormComment?.Contains("噴火", StringComparison.Ordinal) == true
            ? DistantEarthquakeKind.VolcanicEruption
            : DistantEarthquakeKind.Earthquake;
    }

    private static GeoCoordinate? TryCreateCoordinate(
        double? latitude,
        double? longitude)
    {
        return latitude is >= -90 and <= 90 &&
            longitude is >= -180 and <= 180
                ? new GeoCoordinate(latitude.Value, longitude.Value)
                : null;
    }

    private static ReportStatus ParseReportStatus(string? value)
    {
        return value switch
        {
            "None" or "" or null => ReportStatus.Issued,
            "Correction" or "訂正" => ReportStatus.Correction,
            "Cancel" or "Cancelled" or "取消" => ReportStatus.Cancelled,
            _ => ReportStatus.Unknown,
        };
    }

    private static string? BuildTsunamiComment(string? domestic, string? foreign)
    {
        string[] values = new[] { domestic, foreign }
            .Where(value => !string.IsNullOrWhiteSpace(value) && value is not ("None" or "Unknown"))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? null : string.Join("；", values);
    }

    private static DateTimeOffset ParseRequiredTime(string? value, string fieldName)
    {
        return ParseOptionalTime(value)
            ?? throw new FormatException($"P2PQuake {fieldName} 时间无效：{value ?? "缺失"}。");
    }

    private static DateTimeOffset? ParseOptionalTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset withOffset) &&
            (trimmed.EndsWith('Z') || trimmed.Contains('+', StringComparison.Ordinal) ||
             trimmed.LastIndexOf('-') > trimmed.IndexOf('T', StringComparison.Ordinal)))
        {
            return withOffset;
        }

        if (DateTime.TryParseExact(
                trimmed,
                SourceTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime localTime))
        {
            return new DateTimeOffset(localTime, JapanOffset);
        }

        return null;
    }

    private static string? NullIfUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value is "-" or "不明"
            ? null
            : value.Trim();
    }

    private static EarthquakeSourceFetchResult Failure(
        SourceConnectionState state,
        DateTimeOffset checkedAt,
        string detail) => new(
            [],
            new SourceStatus(SourceName, state, checkedAt, Detail: detail));

    private sealed record P2pQuakeItem(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("comments")] P2pComments? Comments,
        [property: JsonPropertyName("issue")] P2pIssue? Issue,
        [property: JsonPropertyName("earthquake")] P2pEarthquake? Earthquake,
        [property: JsonPropertyName("points")] P2pPoint[]? Points,
        [property: JsonPropertyName("time")] string? Time);

    private sealed record P2pComments(
        [property: JsonPropertyName("freeFormComment")] string? FreeFormComment);

    private sealed record P2pIssue(
        [property: JsonPropertyName("correct")] string? Correct,
        [property: JsonPropertyName("time")] string? Time,
        [property: JsonPropertyName("type")] string? Type);

    private sealed record P2pEarthquake(
        [property: JsonPropertyName("domesticTsunami")] string? DomesticTsunami,
        [property: JsonPropertyName("foreignTsunami")] string? ForeignTsunami,
        [property: JsonPropertyName("hypocenter")] P2pHypocenter? Hypocenter,
        [property: JsonPropertyName("maxScale")] int MaxScale,
        [property: JsonPropertyName("time")] string? Time);

    private sealed record P2pHypocenter(
        [property: JsonPropertyName("depth")] int? Depth,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude,
        [property: JsonPropertyName("magnitude")] double? Magnitude,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record P2pPoint(
        [property: JsonPropertyName("addr")] string? Addr,
        [property: JsonPropertyName("isArea")] bool IsArea,
        [property: JsonPropertyName("pref")] string? Prefecture,
        [property: JsonPropertyName("scale")] int Scale);
}
