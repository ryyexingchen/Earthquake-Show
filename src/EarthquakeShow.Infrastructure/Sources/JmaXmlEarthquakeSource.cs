using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class JmaXmlEarthquakeSource : IRealtimeEarthquakeSource, IIncrementalEarthquakeSource
{
    public const string DefaultEndpoint = "https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml";
    public const string DefaultLongEndpoint = "https://www.data.jma.go.jp/developer/xml/feed/eqvol_l.xml";
    private const string SourceName = "jma-xml";
    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";
    private static readonly Regex ReportCodePattern = new(
        "_(?<code>VXSE51|VXSE52|VXSE53)_",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly Uri _longEndpoint;
    private readonly IReadOnlyDictionary<string, GeoCoordinate>? _stationCoordinates;
    private readonly JmaStationCoordinateCatalog? _stationCatalog;
    private readonly JmaIntensityRegionCatalog? _regionCatalog;
    private readonly int _maxEntries;

    public JmaXmlEarthquakeSource(
        HttpClient httpClient,
        IReadOnlyDictionary<string, GeoCoordinate>? stationCoordinates = null,
        string endpoint = DefaultEndpoint,
        int maxEntries = 20,
        JmaStationCoordinateCatalog? stationCatalog = null,
        string longEndpoint = DefaultLongEndpoint,
        JmaIntensityRegionCatalog? regionCatalog = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _stationCoordinates = stationCoordinates;
        _stationCatalog = stationCatalog;
        _regionCatalog = regionCatalog;
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("JMA XML Feed 地址必须是 HTTP 或 HTTPS URL。", nameof(endpoint));
        }

        if (maxEntries is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "Feed 条目数必须在 1 到 100 之间。");
        }

        _endpoint = endpointUri;
        if (!Uri.TryCreate(longEndpoint, UriKind.Absolute, out Uri? longEndpointUri) ||
            longEndpointUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("JMA XML 长期 Feed 地址必须是 HTTP 或 HTTPS URL。", nameof(longEndpoint));
        }

        _longEndpoint = longEndpointUri;
        _maxEntries = maxEntries;
    }

    public string SourceId => SourceName;

    public async Task<EarthquakeSourceFetchResult> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        return await FetchSinceAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EarthquakeSourceFetchResult> FetchSinceAsync(
        DateTimeOffset? since,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using HttpResponseMessage feedResponse = await _httpClient
                .GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!feedResponse.IsSuccessStatusCode)
            {
                return new EarthquakeSourceFetchResult(
                    [],
                    new SourceStatus(
                        SourceId,
                        feedResponse.StatusCode == HttpStatusCode.TooManyRequests
                            ? SourceConnectionState.RateLimited
                            : SourceConnectionState.Disconnected,
                        checkedAt,
                        Detail: $"JMA XML Feed HTTP {(int)feedResponse.StatusCode}"));
            }

            string feedPayload = await feedResponse.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            // 增量回补必须先保留完整 Feed，再按缓存时间筛选，避免把离线期间的报文截掉。
            ImmutableArray<JmaXmlFeedEntry> allEntries = ParseFeedEntries(
                feedPayload,
                since is null ? _maxEntries : int.MaxValue);
            var failures = ImmutableArray.CreateBuilder<string>();
            bool hasRateLimit = false;
            bool hasDisconnected = false;
            bool usedLongFeed = false;
            if (since is not null && IsCoverageIncomplete(allEntries, since))
            {
                try
                {
                    using HttpResponseMessage longFeedResponse = await _httpClient
                        .GetAsync(_longEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    if (!longFeedResponse.IsSuccessStatusCode)
                    {
                        failures.Add($"JMA XML 长期 Feed：HTTP {(int)longFeedResponse.StatusCode}");
                        hasRateLimit |= longFeedResponse.StatusCode == HttpStatusCode.TooManyRequests;
                        hasDisconnected |= longFeedResponse.StatusCode != HttpStatusCode.TooManyRequests;
                    }
                    else
                    {
                        string longFeedPayload = await longFeedResponse.Content
                            .ReadAsStringAsync(cancellationToken)
                            .ConfigureAwait(false);
                        ImmutableArray<JmaXmlFeedEntry> longEntries = ParseFeedEntries(
                            longFeedPayload,
                            since is null ? Math.Max(_maxEntries, 100) : int.MaxValue);
                        allEntries = allEntries
                            .Concat(longEntries)
                            .GroupBy(entry => entry.SourceMessageId, StringComparer.Ordinal)
                            .Select(group => group.First())
                            .OrderByDescending(entry => entry.IssuedAt)
                            .ToImmutableArray();
                        usedLongFeed = true;
                    }
                }
                catch (HttpRequestException exception)
                {
                    failures.Add($"JMA XML 长期 Feed：{exception.Message}");
                    hasDisconnected = true;
                }
            }

            ImmutableArray<JmaXmlFeedEntry> entries = since is null
                ? allEntries
                : allEntries
                    .Where(entry => entry.IssuedAt is null || entry.IssuedAt >= since.Value)
                    .ToImmutableArray();
            var reports = ImmutableArray.CreateBuilder<EarthquakeReport>();
            foreach (JmaXmlFeedEntry entry in entries)
            {
                try
                {
                    using HttpResponseMessage reportResponse = await _httpClient
                        .GetAsync(entry.ReportUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    if (!reportResponse.IsSuccessStatusCode)
                    {
                        failures.Add($"{entry.SourceMessageId}: HTTP {(int)reportResponse.StatusCode}");
                        hasRateLimit |= reportResponse.StatusCode == HttpStatusCode.TooManyRequests;
                        hasDisconnected |= reportResponse.StatusCode != HttpStatusCode.TooManyRequests;
                        continue;
                    }

                    string xml = await reportResponse.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    reports.Add(JmaXmlParser.Parse(
                        xml,
                        new JmaXmlParseOptions(
                            entry.ReportCode,
                            new SourceReference(
                                SourceId,
                                entry.SourceMessageId,
                                entry.ReportUri,
                                xml),
                            ReceivedAt: checkedAt,
                            StationCoordinates: _stationCoordinates,
                            StationCatalog: _stationCatalog,
                            RegionCatalog: _regionCatalog)));
                }
                catch (HttpRequestException exception)
                {
                    failures.Add($"{entry.SourceMessageId}: {exception.Message}");
                    hasDisconnected = true;
                }
                catch (FormatException exception)
                {
                    failures.Add($"{entry.SourceMessageId}: {exception.Message}");
                }
                catch (System.Xml.XmlException exception)
                {
                    failures.Add($"{entry.SourceMessageId}: XML 格式错误：{exception.Message}");
                }
                catch (ArgumentException exception)
                {
                    failures.Add($"{entry.SourceMessageId}: {exception.Message}");
                }
            }

            SourceConnectionState state = failures.Count == 0
                ? IsCoverageIncomplete(allEntries, since)
                    ? SourceConnectionState.Delayed
                    : SourceConnectionState.Online
                : hasRateLimit
                    ? SourceConnectionState.RateLimited
                    : hasDisconnected
                        ? SourceConnectionState.Disconnected
                        : SourceConnectionState.ParseFailed;
            DateTimeOffset? latestReceivedAt = reports.Count == 0
                ? null
                : reports.Max(report => (DateTimeOffset?)report.ReceivedAt);
            string coverage = since is null
                ? $"Feed {allEntries.Length} 条"
                : $"增量起点 {since:O}，{(usedLongFeed ? "长期 Feed 合并后 " : string.Empty)}Feed {allEntries.Length} 条，命中 {entries.Length} 条" +
                    (IsCoverageIncomplete(allEntries, since)
                        ? $"；覆盖可能不足，Feed 最早条目 {GetOldestIssuedAt(allEntries):O}"
                        : "；覆盖起点正常");
            string detail = failures.Count == 0
                ? $"JMA XML：成功 {reports.Count} 条；{coverage}"
                : $"JMA XML：成功 {reports.Count} 条，失败 {failures.Count} 条；{coverage}；{string.Join("；", failures.Take(3))}";
            return new EarthquakeSourceFetchResult(
                reports.ToImmutable(),
                new SourceStatus(SourceId, state, checkedAt, latestReceivedAt, detail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Xml.XmlException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"JMA XML Feed 格式错误：{exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(SourceConnectionState.Disconnected, checkedAt, $"JMA XML 网络错误：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"JMA XML Feed 字段错误：{exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"JMA XML Feed 字段越界：{exception.Message}");
        }
    }

    internal static ImmutableArray<JmaXmlFeedEntry> ParseFeedEntries(
        string payload,
        int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        XDocument document = XDocument.Parse(payload, LoadOptions.PreserveWhitespace);
        return document
            .Descendants(AtomNamespace + "entry")
            .Select(ParseEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderByDescending(entry => entry.IssuedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.SourceMessageId, StringComparer.Ordinal)
            .Take(maxEntries)
            .ToImmutableArray();
    }

    private static JmaXmlFeedEntry? ParseEntry(XElement entry)
    {
        string? id = entry.Element(AtomNamespace + "id")?.Value.Trim();
        string? href = entry
            .Elements(AtomNamespace + "link")
            .Where(link => string.Equals(
                (string?)link.Attribute("type"),
                "application/xml",
                StringComparison.OrdinalIgnoreCase))
            .Select(link => (string?)link.Attribute("href"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href) ||
            !Uri.TryCreate(href, UriKind.Absolute, out Uri? reportUri))
        {
            return null;
        }

        Match match = ReportCodePattern.Match(id);
        if (!match.Success || !Uri.TryCreate(id, UriKind.Absolute, out Uri? idUri))
        {
            return null;
        }

        string sourceMessageId = idUri.Segments[^1];
        return new JmaXmlFeedEntry(
            sourceMessageId,
            match.Groups["code"].Value,
            reportUri,
            ParseMessageIssuedAt(sourceMessageId));
    }

    private static DateTimeOffset? ParseMessageIssuedAt(string sourceMessageId)
    {
        if (sourceMessageId.Length < 14 ||
            !DateTime.TryParseExact(
                sourceMessageId[..14],
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime localTime))
        {
            return null;
        }

        return new DateTimeOffset(localTime, TimeSpan.FromHours(9));
    }

    private static bool IsCoverageIncomplete(
        ImmutableArray<JmaXmlFeedEntry> entries,
        DateTimeOffset? since)
    {
        DateTimeOffset? oldest = GetOldestIssuedAt(entries);
        return since is not null && oldest is not null && oldest > since.Value;
    }

    private static DateTimeOffset? GetOldestIssuedAt(
        ImmutableArray<JmaXmlFeedEntry> entries) => entries
        .Where(entry => entry.IssuedAt is not null)
        .Select(entry => entry.IssuedAt)
        .Min();

    private static EarthquakeSourceFetchResult Failure(
        SourceConnectionState state,
        DateTimeOffset checkedAt,
        string detail) => new(
            [],
            new SourceStatus(SourceName, state, checkedAt, Detail: detail));

    internal sealed record JmaXmlFeedEntry(
        string SourceMessageId,
        string ReportCode,
        Uri ReportUri,
        DateTimeOffset? IssuedAt = null);
}
