using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class NtoolYahooRealtimeObservationSource : IRealtimeObservationSource
{
    public const string DefaultSiteListEndpoint =
        "https://weather-kyoshin.east.edge.storage-yahoo.jp/SiteList/sitelist.json";

    public const string DefaultRealtimeDataEndpoint =
        "https://weather-kyoshin.east.edge.storage-yahoo.jp/RealTimeData";

    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);
    private readonly HttpClient _httpClient;
    private readonly Uri _siteListEndpoint;
    private readonly Uri _realtimeDataEndpoint;
    private readonly Func<DateTimeOffset> _clock;
    private RealtimeObservationSiteCatalog? _siteCatalog;

    public NtoolYahooRealtimeObservationSource(
        HttpClient httpClient,
        string siteListEndpoint = DefaultSiteListEndpoint,
        string realtimeDataEndpoint = DefaultRealtimeDataEndpoint,
        Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _siteListEndpoint = CreateHttpUri(siteListEndpoint, nameof(siteListEndpoint));
        _realtimeDataEndpoint = CreateHttpUri(
            realtimeDataEndpoint,
            nameof(realtimeDataEndpoint));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string SourceId => "ntool-yahoo-realtime";

    public async Task<RealtimeObservationFetchResult> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset checkedAt = _clock().ToUniversalTime();
        try
        {
            if (_siteCatalog is null)
            {
                using HttpResponseMessage siteResponse = await _httpClient.GetAsync(
                    BuildSiteListUri(checkedAt),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (!siteResponse.IsSuccessStatusCode)
                {
                    return Failure(
                        siteResponse.StatusCode == HttpStatusCode.TooManyRequests
                            ? SourceConnectionState.RateLimited
                            : SourceConnectionState.Disconnected,
                        checkedAt,
                        $"nTool/Yahoo 站点目录 HTTP {(int)siteResponse.StatusCode}");
                }

                string sitePayload = await ReadJsonPayloadAsync(
                    siteResponse,
                    cancellationToken).ConfigureAwait(false);
                _siteCatalog = ParseSiteCatalog(sitePayload);
            }

            DateTimeOffset requestedAt = checkedAt.ToOffset(JapanOffset);
            for (int offsetSeconds = 0; offsetSeconds <= 8; offsetSeconds++)
            {
                DateTimeOffset sampleTime = requestedAt.AddSeconds(-offsetSeconds);
                Uri dataUri = BuildDataUri(sampleTime);
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    dataUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Failure(
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? SourceConnectionState.RateLimited
                            : SourceConnectionState.Disconnected,
                        checkedAt,
                        $"nTool/Yahoo 实时观测 HTTP {(int)response.StatusCode}");
                }

                string payload = await ReadJsonPayloadAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                ImmutableArray<RealtimeObservationStation> stations = ParseRealtimeData(
                    payload,
                    _siteCatalog,
                    sampleTime,
                    checkedAt,
                    SourceId);
                SourceConnectionState state = sampleTime < requestedAt
                    ? SourceConnectionState.Delayed
                    : SourceConnectionState.Online;
                return new RealtimeObservationFetchResult(
                    stations,
                    new SourceStatus(
                        SourceId,
                        state,
                        checkedAt,
                        checkedAt,
                        $"nTool/Yahoo：{stations.Length} 个站点，样本 {sampleTime:HH:mm:ss} JST"));
            }

            return Failure(
                SourceConnectionState.Delayed,
                checkedAt,
                "nTool/Yahoo 实时观测样本尚未生成");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"nTool/Yahoo JSON 格式错误：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"nTool/Yahoo 字段错误：{exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(SourceConnectionState.Disconnected, checkedAt, $"nTool/Yahoo 网络错误：{exception.Message}");
        }
        catch (IOException exception)
        {
            return Failure(SourceConnectionState.Disconnected, checkedAt, $"nTool/Yahoo 读取错误：{exception.Message}");
        }
    }

    public static RealtimeObservationSiteCatalog ParseSiteCatalog(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("items", out JsonElement itemsElement))
        {
            string siteConfigId = ReadRequiredString(root, "siteConfigId");
            if (itemsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("nTool/Yahoo 站点目录的 items 必须是数组。");
            }

            var realtimeSites = ImmutableArray.CreateBuilder<RealtimeObservationSite>();
            int index = 0;
            foreach (JsonElement item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
                {
                    throw new FormatException($"nTool/Yahoo 站点目录第 {index} 项不是 [纬度, 经度]。");
                }

                double latitude = ReadNumber(item[0]);
                double longitude = ReadNumber(item[1]);
                if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
                    latitude is < -90 or > 90 || longitude is < -180 or > 180)
                {
                    throw new FormatException($"nTool/Yahoo 站点目录第 {index} 项坐标无效。");
                }

                realtimeSites.Add(new RealtimeObservationSite(
                    $"site-index:{index}",
                    $"观测点 {index + 1}",
                    new GeoCoordinate(latitude, longitude),
                    index));
                index++;
            }

            if (realtimeSites.Count == 0)
            {
                throw new FormatException("nTool/Yahoo 站点目录没有有效站点。");
            }

            return new RealtimeObservationSiteCatalog(siteConfigId, realtimeSites.ToImmutable());
        }

        JsonElement array = FindArray(root, "sites", "stations", "site");
        var sites = ImmutableArray.CreateBuilder<RealtimeObservationSite>();
        int legacyIndex = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string code = ReadString(element, "id", "code", "stationCode", "siteCode");
            string name = ReadOptionalString(element, "name", "stationName", "title") ?? code;
            double latitude = ReadDouble(element, "lat", "latitude");
            double longitude = ReadDouble(element, "lon", "lng", "longitude");
            if (code.Length == 0 ||
                !double.IsFinite(latitude) ||
                !double.IsFinite(longitude) ||
                latitude is < -90 or > 90 ||
                longitude is < -180 or > 180)
            {
                continue;
            }

            sites.Add(new RealtimeObservationSite(
                code,
                name,
                new GeoCoordinate(latitude, longitude),
                legacyIndex));
            legacyIndex++;
        }

        if (sites.Count == 0)
        {
            throw new FormatException("nTool/Yahoo 站点目录没有有效站点。");
        }

        return new RealtimeObservationSiteCatalog(
            "legacy",
            sites
            .GroupBy(site => site.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToImmutableArray());
    }

    public static ImmutableArray<RealtimeObservationSite> ParseSiteList(string payload) =>
        ParseSiteCatalog(payload).Sites;

    public static ImmutableArray<RealtimeObservationStation> ParseRealtimeData(
        string payload,
        RealtimeObservationSiteCatalog siteCatalog,
        DateTimeOffset sampledAt,
        DateTimeOffset receivedAt,
        string sourceId = "ntool-yahoo-realtime")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentNullException.ThrowIfNull(siteCatalog);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement realtimeData = document.RootElement;
        if (realtimeData.ValueKind == JsonValueKind.Object &&
            realtimeData.TryGetProperty("realTimeData", out JsonElement nested))
        {
            realtimeData = nested;
        }

        if (realtimeData.ValueKind != JsonValueKind.Object ||
            !realtimeData.TryGetProperty("intensity", out JsonElement intensityElement))
        {
            throw new FormatException("nTool/Yahoo 实时数据缺少 intensity 字段。");
        }

        bool legacyArray = siteCatalog.SiteConfigId == "legacy" &&
            intensityElement.ValueKind == JsonValueKind.Array;
        if (!legacyArray &&
            (!realtimeData.TryGetProperty("siteConfigId", out JsonElement configElement) ||
             configElement.ValueKind != JsonValueKind.String ||
             !string.Equals(configElement.GetString(), siteCatalog.SiteConfigId, StringComparison.Ordinal)))
        {
            throw new FormatException("nTool/Yahoo 站点表版本与实时数据不一致。");
        }

        string intensityText = intensityElement.ValueKind == JsonValueKind.String
            ? intensityElement.GetString() ?? string.Empty
            : string.Empty;
        if (!legacyArray && intensityText.Length != siteCatalog.Sites.Length)
        {
            throw new FormatException(
                $"nTool/Yahoo intensity 长度与站点数不一致：intensity={intensityText.Length}, stations={siteCatalog.Sites.Length}");
        }

        var result = ImmutableArray.CreateBuilder<RealtimeObservationStation>();
        int index = 0;
        if (legacyArray)
        {
            int count = Math.Min(intensityElement.GetArrayLength(), siteCatalog.Sites.Length);
            for (int legacyIndex = 0; legacyIndex < count; legacyIndex++)
            {
                if (TryParseIntensity(intensityElement[legacyIndex], out JmaIntensity intensity, out bool isZero))
                {
                    RealtimeObservationSite site = siteCatalog.Sites[legacyIndex];
                    result.Add(new RealtimeObservationStation(
                        site.Code,
                        site.Name,
                        site.Coordinate,
                        intensity,
                        isZero,
                        sampledAt,
                        receivedAt,
                        RealtimeObservationQuality.Valid,
                        sourceId));
                }
            }
        }
        else
        {
            foreach (char value in intensityText)
            {
                if (TryParseIntensity(value.ToString(), out JmaIntensity intensity, out bool isZero))
                {
                    RealtimeObservationSite site = siteCatalog.Sites[index];
                    result.Add(new RealtimeObservationStation(
                        site.Code,
                        site.Name,
                        site.Coordinate,
                        intensity,
                        isZero,
                        sampledAt,
                        receivedAt,
                        RealtimeObservationQuality.Valid,
                        sourceId));
                }

                index++;
            }
        }

        return result.ToImmutable();
    }

    public static ImmutableArray<RealtimeObservationStation> ParseRealtimeData(
        string payload,
        IReadOnlyList<RealtimeObservationSite> sites,
        DateTimeOffset sampledAt,
        DateTimeOffset receivedAt,
        string sourceId = "ntool-yahoo-realtime") =>
        ParseRealtimeData(
            payload,
            new RealtimeObservationSiteCatalog("legacy", sites.ToImmutableArray()),
            sampledAt,
            receivedAt,
            sourceId);

    private Uri BuildDataUri(DateTimeOffset sampledAt)
    {
        string relative = $"{sampledAt:yyyyMMdd}/{sampledAt:yyyyMMddHHmmss}.json";
        return new Uri(
            _realtimeDataEndpoint.AbsoluteUri.TrimEnd('/') + "/" + relative,
            UriKind.Absolute);
    }

    private static bool TryParseIntensity(
        JsonElement value,
        out JmaIntensity intensity,
        out bool isZero)
    {
        string text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => string.Empty,
        };
        return TryParseIntensity(text, out intensity, out isZero);
    }

    private static bool TryParseIntensity(
        string rawValue,
        out JmaIntensity intensity,
        out bool isZero)
    {
        string text = rawValue;
        text = text.Trim().Replace("強", "+", StringComparison.Ordinal)
            .Replace("弱", "-", StringComparison.Ordinal);
        if (text is "0" or "0.0")
        {
            intensity = JmaIntensity.Unknown;
            isZero = true;
            return true;
        }

        if (text.Length == 1)
        {
            int level = text[0] - 'd';
            if (level is >= 0 and <= 20)
            {
                if (level <= 7)
                {
                    intensity = JmaIntensity.Unknown;
                    isZero = true;
                    return true;
                }

                isZero = false;
                intensity = level switch
                {
                    <= 9 => JmaIntensity.One,
                    <= 11 => JmaIntensity.Two,
                    <= 13 => JmaIntensity.Three,
                    <= 15 => JmaIntensity.Four,
                    16 => JmaIntensity.FiveLower,
                    17 => JmaIntensity.FiveUpper,
                    18 => JmaIntensity.SixLower,
                    19 => JmaIntensity.SixUpper,
                    _ => JmaIntensity.Seven,
                };
                return true;
            }
        }

        isZero = false;
        if (text is "1" or "1.0")
        {
            intensity = JmaIntensity.One;
            return true;
        }

        if (text is "2" or "2.0")
        {
            intensity = JmaIntensity.Two;
            return true;
        }

        if (text is "3" or "3.0")
        {
            intensity = JmaIntensity.Three;
            return true;
        }

        if (text is "4" or "4.0")
        {
            intensity = JmaIntensity.Four;
            return true;
        }

        intensity = text switch
        {
            "5-" or "5.0-" => JmaIntensity.FiveLower,
            "5+" or "5.0+" => JmaIntensity.FiveUpper,
            "6-" or "6.0-" => JmaIntensity.SixLower,
            "6+" or "6.0+" => JmaIntensity.SixUpper,
            "7" or "7.0" => JmaIntensity.Seven,
            _ => JmaIntensity.Unknown,
        };
        return intensity != JmaIntensity.Unknown;
    }

    private static JsonElement FindArray(JsonElement root, params string[] names)
    {
        JsonElement value = FindProperty(root, names);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("nTool/Yahoo 站点目录必须包含数组。");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!.Trim();
        }

        throw new FormatException($"nTool/Yahoo 站点目录缺少字段：{name}。");
    }

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return double.NaN;
    }

    private Uri BuildSiteListUri(DateTimeOffset checkedAt)
    {
        var builder = new UriBuilder(_siteListEndpoint);
        string query = builder.Query.TrimStart('?');
        string cacheBuster = $"time={checkedAt.ToUnixTimeMilliseconds()}";
        builder.Query = string.IsNullOrEmpty(query) ? cacheBuster : $"{query}&{cacheBuster}";
        return builder.Uri;
    }

    private static async Task<string> ReadJsonPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Any(value =>
                string.Equals(value, "gzip", StringComparison.OrdinalIgnoreCase)) ||
            (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b))
        {
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            await gzip.CopyToAsync(decompressed, cancellationToken).ConfigureAwait(false);
            bytes = decompressed.ToArray();
        }

        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    private static JsonElement FindProperty(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out JsonElement value))
            {
                return value;
            }
        }

        throw new FormatException($"nTool/Yahoo JSON 缺少字段：{string.Join('/', names)}。");
    }

    private static string ReadString(JsonElement element, params string[] names) =>
        ReadOptionalString(element, names) ?? string.Empty;

    private static string? ReadOptionalString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim();
            }
        }

        return null;
    }

    private static double ReadDouble(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return double.NaN;
    }

    private RealtimeObservationFetchResult Failure(
        SourceConnectionState state,
        DateTimeOffset checkedAt,
        string detail) => new(
        [],
        new SourceStatus(SourceId, state, checkedAt, Detail: detail));

    public sealed record RealtimeObservationSite(
        string Code,
        string Name,
        GeoCoordinate Coordinate,
        int Index = -1);

    public sealed record RealtimeObservationSiteCatalog(
        string SiteConfigId,
        ImmutableArray<RealtimeObservationSite> Sites);

    private static Uri CreateHttpUri(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("实时观测地址必须是 HTTP 或 HTTPS URL。", parameterName);
        }

        return uri;
    }
}
