using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class JmaJsonEarthquakeSource : IRealtimeEarthquakeSource
{
    public const string DefaultEndpoint = "https://www.jma.go.jp/bosai/quake/data/list.json";
    private const string SourceName = "jma-json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex CoordinatePattern = new(
        "^(?<latitude>[+-]?[0-9]+(?:\\.[0-9]+)?)(?<longitude>[+-][0-9]+(?:\\.[0-9]+)?)(?<depth>[+-][0-9]+)?/",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] DateTimeFormats =
    [
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public JmaJsonEarthquakeSource(
        HttpClient httpClient,
        string endpoint = DefaultEndpoint)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("JMA JSON 地址必须是 HTTP 或 HTTPS URL。", nameof(endpoint));
        }

        _endpoint = endpointUri;
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
                        Detail: $"JMA JSON HTTP {(int)response.StatusCode}"));
            }

            string payload = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            ImmutableArray<EarthquakeReport> reports = ParseReports(
                payload,
                checkedAt,
                _endpoint);
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
                    $"JMA JSON：{reports.Length} 条"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return Failure(
                SourceConnectionState.ParseFailed,
                checkedAt,
                $"JMA JSON 格式错误：{exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(
                SourceConnectionState.Disconnected,
                checkedAt,
                $"JMA JSON 网络错误：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return Failure(
                SourceConnectionState.ParseFailed,
                checkedAt,
                $"JMA JSON 字段错误：{exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return Failure(
                SourceConnectionState.ParseFailed,
                checkedAt,
                $"JMA JSON 字段越界：{exception.Message}");
        }
        catch (IOException exception)
        {
            return Failure(
                SourceConnectionState.Disconnected,
                checkedAt,
                $"JMA JSON 读取错误：{exception.Message}");
        }
    }

    internal static ImmutableArray<EarthquakeReport> ParseReports(
        string payload,
        DateTimeOffset receivedAt,
        Uri endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        JmaJsonItem[]? items = JsonSerializer.Deserialize<JmaJsonItem[]>(payload, JsonOptions);
        if (items is null)
        {
            throw new JsonException("JMA JSON 顶层必须是数组。");
        }

        var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
        foreach (JmaJsonItem item in items)
        {
            reports.Add(ToReport(item, payload, receivedAt, endpoint));
        }

        return reports.ToImmutable();
    }

    private static EarthquakeReport ToReport(
        JmaJsonItem item,
        string rawPayload,
        DateTimeOffset receivedAt,
        Uri endpoint)
    {
        if (string.IsNullOrWhiteSpace(item.EventId))
        {
            throw new FormatException("JMA JSON 缺少 eid。");
        }

        DateTimeOffset issuedAt = ParseSourceTime(item.ReportTime, "rdt");
        DateTimeOffset? originTime = ParseOptionalSourceTime(item.OriginTime);
        (GeoCoordinate? coordinate, int? depthKm) = ParseCoordinate(item.Coordinate);
        string? regionName = NullIfUnknown(item.Region);
        Hypocenter? hypocenter = coordinate is null && regionName is null && depthKm is null
            ? null
            : new Hypocenter(regionName, NullIfUnknown(item.AreaCode), coordinate, depthKm);
        double? magnitudeValue = ParseMagnitude(item.Magnitude);
        Magnitude? magnitude = magnitudeValue is null
            ? null
            : new Magnitude(magnitudeValue);
        JmaIntensity maxIntensity = ParseIntensity(item.MaxIntensity);
        string messageId = NullIfUnknown(item.RawFileName)
            ?? $"{item.ControlTime ?? issuedAt.ToString("O", CultureInfo.InvariantCulture)}_{item.EventId}";

        return new EarthquakeReport
        {
            EventId = item.EventId,
            ReportCode = "JMA-JSON",
            ReportType = ParseReportType(item.Title),
            Status = ParseReportStatus(item.InformationType),
            Context = ReportContext.Normal,
            Serial = ParseSerial(item.Serial),
            OriginTime = originTime,
            IssuedAt = issuedAt,
            ReceivedAt = receivedAt,
            Hypocenter = hypocenter,
            Magnitude = magnitude,
            MaxIntensity = maxIntensity,
            Source = new SourceReference(
                SourceName,
                messageId,
                endpoint,
                rawPayload),
        };
    }

    private static EarthquakeSourceFetchResult Failure(
        SourceConnectionState state,
        DateTimeOffset checkedAt,
        string detail) => new(
            [],
            new SourceStatus(SourceName, state, checkedAt, Detail: detail));

    private static DateTimeOffset ParseSourceTime(string? value, string fieldName)
    {
        if (ParseOptionalSourceTime(value) is DateTimeOffset result)
        {
            return result;
        }

        throw new FormatException($"JMA JSON {fieldName} 时间无效：{value ?? "缺失"}。");
    }

    private static DateTimeOffset? ParseOptionalSourceTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (HasExplicitOffset(trimmed) &&
            DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset withOffset))
        {
            return withOffset;
        }

        if (DateTime.TryParseExact(
                value,
                DateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime localTime))
        {
            TimeSpan japanOffset = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time")
                .GetUtcOffset(localTime);
            return new DateTimeOffset(localTime, japanOffset);
        }

        return null;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z'))
        {
            return true;
        }

        return value.Length >= 6 &&
            value[^3] == ':' &&
            value[^6] is '+' or '-';
    }

    private static (GeoCoordinate? Coordinate, int? DepthKm) ParseCoordinate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "-" or "不明")
        {
            return (null, null);
        }

        Match match = CoordinatePattern.Match(value.Trim());
        if (!match.Success)
        {
            throw new FormatException($"JMA JSON cod 坐标无效：{value}。");
        }

        double latitude = double.Parse(match.Groups["latitude"].Value, CultureInfo.InvariantCulture);
        double longitude = double.Parse(match.Groups["longitude"].Value, CultureInfo.InvariantCulture);
        int? depthKm = null;
        if (match.Groups["depth"].Success &&
            int.TryParse(
                match.Groups["depth"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int depthMeters))
        {
            depthKm = (int)Math.Round(
                Math.Abs(depthMeters) / 1000d,
                MidpointRounding.AwayFromZero);
        }

        return (new GeoCoordinate(latitude, longitude), depthKm);
    }

    private static double? ParseMagnitude(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "-" or "不明")
        {
            return null;
        }

        string normalized = value.Trim().TrimStart('M', 'm');
        return TryParseDouble(normalized, out double magnitude) ? magnitude : null;
    }

    private static JmaIntensity ParseIntensity(string? value)
    {
        string normalized = NullIfUnknown(value) ?? "unknown";
        normalized = normalized switch
        {
            "5弱" => "5-lower",
            "5強" => "5-upper",
            "6弱" => "6-lower",
            "6強" => "6-upper",
            _ => normalized,
        };
        return JmaIntensityExtensions.TryParseCode(normalized, out JmaIntensity result)
            ? result
            : JmaIntensity.Unknown;
    }

    private static EarthquakeReportType ParseReportType(string? value)
    {
        return value?.Trim() switch
        {
            "震度速報" => EarthquakeReportType.SeismicIntensity,
            "震源情報" => EarthquakeReportType.Hypocenter,
            "震源・震度情報" => EarthquakeReportType.HypocenterAndIntensity,
            _ => EarthquakeReportType.Unknown,
        };
    }

    private static ReportStatus ParseReportStatus(string? value)
    {
        return value?.Trim() switch
        {
            "発表" => ReportStatus.Issued,
            "訂正" => ReportStatus.Correction,
            "取消" or "取り消し" => ReportStatus.Cancelled,
            _ => ReportStatus.Unknown,
        };
    }

    private static int? ParseSerial(string? value)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int result)
            ? result
            : null;
    }

    private static string? NullIfUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value is "-" or "不明"
            ? null
            : value.Trim();
    }

    private static bool TryParseDouble(string? value, out double result)
    {
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result) && double.IsFinite(result);
    }

    private sealed class JmaJsonItem
    {
        [JsonPropertyName("eid")]
        public string? EventId { get; set; }

        [JsonPropertyName("ctt")]
        public string? ControlTime { get; set; }

        [JsonPropertyName("rdt")]
        public string? ReportTime { get; set; }

        [JsonPropertyName("at")]
        public string? OriginTime { get; set; }

        [JsonPropertyName("ttl")]
        public string? Title { get; set; }

        [JsonPropertyName("ift")]
        public string? InformationType { get; set; }

        [JsonPropertyName("ser")]
        public string? Serial { get; set; }

        [JsonPropertyName("mag")]
        public string? Magnitude { get; set; }

        [JsonPropertyName("maxi")]
        public string? MaxIntensity { get; set; }

        [JsonPropertyName("anm")]
        public string? Region { get; set; }

        [JsonPropertyName("acd")]
        public string? AreaCode { get; set; }

        [JsonPropertyName("cod")]
        public string? Coordinate { get; set; }

        [JsonPropertyName("json")]
        public string? RawFileName { get; set; }
    }
}
