using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class JmaTsunamiXmlSource : IRealtimeTsunamiSource, IIncrementalTsunamiSource
{
    public const string DefaultEndpoint = "https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml";
    public const string DefaultLongEndpoint = "https://www.data.jma.go.jp/developer/xml/feed/eqvol_l.xml";

    private const string SourceName = "jma-xml-tsunami";
    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";
    private static readonly Regex ReportCodePattern = new(
        "_(?<code>VTSE41|VTSE51|VTSE52)_",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly Uri _longEndpoint;
    private readonly int _maxEntries;

    public JmaTsunamiXmlSource(
        HttpClient httpClient,
        string endpoint = DefaultEndpoint,
        int maxEntries = 20,
        string longEndpoint = DefaultLongEndpoint)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = ValidateEndpoint(endpoint, nameof(endpoint), "JMA 海啸 XML Feed 地址");
        _longEndpoint = ValidateEndpoint(longEndpoint, nameof(longEndpoint), "JMA 海啸 XML 长期 Feed 地址");
        if (maxEntries is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "Feed 条目数必须在 1 到 100 之间。");
        }

        _maxEntries = maxEntries;
    }

    public string SourceId => SourceName;

    public Task<TsunamiSourceFetchResult> FetchAsync(
        CancellationToken cancellationToken = default) =>
        FetchSinceAsync(null, cancellationToken);

    public async Task<TsunamiSourceFetchResult> FetchSinceAsync(
        DateTimeOffset? since,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        try
        {
            HttpResponseMessage feedResponse = await _httpClient
                .GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            using (feedResponse)
            {
                if (!feedResponse.IsSuccessStatusCode)
                {
                    return Failure(
                        feedResponse.StatusCode == HttpStatusCode.TooManyRequests
                            ? SourceConnectionState.RateLimited
                            : SourceConnectionState.Disconnected,
                        checkedAt,
                        $"JMA 海啸 XML Feed HTTP {(int)feedResponse.StatusCode}");
                }

                string feedPayload = await feedResponse.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                ImmutableArray<JmaTsunamiFeedEntry> allEntries = ParseFeedEntries(feedPayload, _maxEntries);
                var failures = ImmutableArray.CreateBuilder<string>();
                bool hasRateLimit = false;
                bool hasDisconnected = false;
                bool usedLongFeed = false;

                if (since is not null && IsCoverageIncomplete(allEntries, since))
                {
                    try
                    {
                        HttpResponseMessage longResponse = await _httpClient
                            .GetAsync(_longEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                            .ConfigureAwait(false);
                        using (longResponse)
                        {
                            if (!longResponse.IsSuccessStatusCode)
                            {
                                failures.Add($"JMA 海啸 XML 长期 Feed：HTTP {(int)longResponse.StatusCode}");
                                hasRateLimit |= longResponse.StatusCode == HttpStatusCode.TooManyRequests;
                                hasDisconnected |= longResponse.StatusCode != HttpStatusCode.TooManyRequests;
                            }
                            else
                            {
                                string longPayload = await longResponse.Content
                                    .ReadAsStringAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                ImmutableArray<JmaTsunamiFeedEntry> longEntries = ParseFeedEntries(
                                    longPayload,
                                    Math.Max(_maxEntries, 100));
                                allEntries = allEntries
                                    .Concat(longEntries)
                                    .GroupBy(entry => entry.SourceMessageId, StringComparer.Ordinal)
                                    .Select(group => group.First())
                                    .OrderByDescending(entry => entry.IssuedAt)
                                    .ToImmutableArray();
                                usedLongFeed = true;
                            }
                        }
                    }
                    catch (HttpRequestException exception)
                    {
                        failures.Add($"JMA 海啸 XML 长期 Feed：{exception.Message}");
                        hasDisconnected = true;
                    }
                }

                ImmutableArray<JmaTsunamiFeedEntry> entries = since is null
                    ? allEntries
                    : allEntries
                        .Where(entry => entry.IssuedAt is null || entry.IssuedAt >= since.Value)
                        .ToImmutableArray();
                var reports = ImmutableArray.CreateBuilder<JmaTsunamiReport>();
                foreach (JmaTsunamiFeedEntry entry in entries)
                {
                    try
                    {
                        HttpResponseMessage reportResponse = await _httpClient
                            .GetAsync(entry.ReportUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                            .ConfigureAwait(false);
                        using (reportResponse)
                        {
                            if (!reportResponse.IsSuccessStatusCode)
                            {
                                failures.Add($"{entry.SourceMessageId}: HTTP {(int)reportResponse.StatusCode}");
                                hasRateLimit |= reportResponse.StatusCode == HttpStatusCode.TooManyRequests;
                                hasDisconnected |= reportResponse.StatusCode != HttpStatusCode.TooManyRequests;
                                continue;
                            }

                            string xml = await reportResponse.Content
                                .ReadAsStringAsync(cancellationToken)
                                .ConfigureAwait(false);
                            reports.Add(JmaTsunamiXmlParser.Parse(
                                xml,
                                new JmaTsunamiXmlParseOptions(
                                    entry.ReportCode,
                                    new SourceReference(SourceId, entry.SourceMessageId, entry.ReportUri, xml),
                                    checkedAt)));
                        }
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
                    ? $"JMA 海啸 XML：成功 {reports.Count} 条；{coverage}"
                    : $"JMA 海啸 XML：成功 {reports.Count} 条，失败 {failures.Count} 条；{coverage}；{string.Join("；", failures.Take(3))}";
                return new TsunamiSourceFetchResult(
                    reports.ToImmutable(),
                    new SourceStatus(SourceId, state, checkedAt, latestReceivedAt, detail));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Xml.XmlException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"JMA 海啸 XML Feed 格式错误：{exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(SourceConnectionState.Disconnected, checkedAt, $"JMA 海啸 XML 网络错误：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"JMA 海啸 XML Feed 字段错误：{exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return Failure(SourceConnectionState.ParseFailed, checkedAt, $"JMA 海啸 XML Feed 字段越界：{exception.Message}");
        }
    }

    internal static ImmutableArray<JmaTsunamiFeedEntry> ParseFeedEntries(
        string payload,
        int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (maxEntries is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        XDocument document = XDocument.Parse(payload, LoadOptions.PreserveWhitespace);
        return document
            .Descendants(AtomNamespace + "entry")
            .Select(ParseEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .Take(maxEntries)
            .ToImmutableArray();
    }

    private static JmaTsunamiFeedEntry? ParseEntry(XElement entry)
    {
        string? id = entry.Element(AtomNamespace + "id")?.Value.Trim();
        string? href = entry
            .Elements(AtomNamespace + "link")
            .Where(link => string.Equals((string?)link.Attribute("type"), "application/xml", StringComparison.OrdinalIgnoreCase))
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
        return new JmaTsunamiFeedEntry(
            sourceMessageId,
            match.Groups["code"].Value,
            reportUri,
            ParseMessageIssuedAt(sourceMessageId));
    }

    private static DateTimeOffset? ParseMessageIssuedAt(string sourceMessageId)
    {
        if (sourceMessageId.Length < 14 ||
            !DateTime.TryParseExact(sourceMessageId[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime localTime))
        {
            return null;
        }

        return new DateTimeOffset(localTime, TimeSpan.FromHours(9));
    }

    private static bool IsCoverageIncomplete(ImmutableArray<JmaTsunamiFeedEntry> entries, DateTimeOffset? since) =>
        since is not null && GetOldestIssuedAt(entries) is DateTimeOffset oldest && oldest > since.Value;

    private static DateTimeOffset? GetOldestIssuedAt(ImmutableArray<JmaTsunamiFeedEntry> entries) =>
        entries.Where(entry => entry.IssuedAt is not null).Select(entry => entry.IssuedAt).Min();

    private static Uri ValidateEndpoint(string endpoint, string parameterName, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"{description}必须是 HTTP 或 HTTPS URL。", parameterName);
        }

        return uri;
    }

    private static TsunamiSourceFetchResult Failure(SourceConnectionState state, DateTimeOffset checkedAt, string detail) =>
        new([], new SourceStatus(SourceName, state, checkedAt, Detail: detail));

    internal sealed record JmaTsunamiFeedEntry(
        string SourceMessageId,
        string ReportCode,
        Uri ReportUri,
        DateTimeOffset? IssuedAt = null);
}
