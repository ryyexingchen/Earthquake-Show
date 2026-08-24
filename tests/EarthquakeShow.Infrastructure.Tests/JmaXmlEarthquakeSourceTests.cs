using System.Net;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class JmaXmlEarthquakeSourceTests
{
    [Fact]
    public async Task Fetch_FeedEntry_ParsesSupportedXmlWithSourceIdentity()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "TestData");
        string xmlPath = Path.Combine(
            root,
            "JmaXml",
            "Official",
            "20260818221432_0_VXSE53_270000.xml");
        string xml = await File.ReadAllTextAsync(xmlPath);
        const string reportUri = "https://example.test/20260818221432_0_VXSE53_270000.xml";
        string feed = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260818221432_0_VPWS50_010000.xml</id>
                <link type="application/xml" href="https://example.test/weather.xml" />
              </entry>
              <entry>
                <id>{reportUri}</id>
                <link type="application/xml" href="{reportUri}" />
              </entry>
            </feed>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
        {
            if (uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, feed);
            }

            return Response(HttpStatusCode.OK, xml);
        }));
        string stationPath = Path.Combine(root, "JmaStations.csv");
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            JmaStationCatalog.LoadFile(stationPath),
            "https://example.test/feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        EarthquakeReport report = Assert.Single(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Equal("VXSE53", report.ReportCode);
        Assert.Equal("20260818221432_0_VXSE53_270000.xml", report.Source.SourceMessageId);
        Assert.Equal(75, report.IntensityStations.Length);
        Assert.All(report.IntensityStations, station => Assert.NotNull(station.Coordinate));
        Assert.Contains("<Report", report.Source.SourcePayload);
    }

    [Fact]
    public async Task Fetch_ReportFailure_ReturnsDisconnectedStatus()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260818221432_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/report.xml" />
              </entry>
            </feed>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
            uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, feed)
                : Response(HttpStatusCode.InternalServerError)));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.Disconnected, result.Status.State);
        Assert.Contains("失败 1 条", result.Status.Detail);
    }

    [Fact]
    public async Task FetchSince_FiltersFeedEntriesBeforeCachedIssuedAt()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "TestData");
        string xmlPath = Path.Combine(
            root,
            "JmaXml",
            "Official",
            "20260818221432_0_VXSE53_270000.xml");
        string xml = await File.ReadAllTextAsync(xmlPath);
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260818221317_0_VXSE52_270000.xml</id>
                <link type="application/xml" href="https://example.test/old.xml" />
              </entry>
              <entry>
                <id>https://example.test/20260818221432_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/new.xml" />
              </entry>
            </feed>
            """;
        const string longFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260818221317_0_VXSE52_270000.xml</id>
                <link type="application/xml" href="https://example.test/old.xml" />
              </entry>
              <entry>
                <id>https://example.test/20260818221432_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/new.xml" />
              </entry>
            </feed>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
        {
            if (uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, feed);
            }

            if (uri.AbsoluteUri.EndsWith("long-feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, longFeed);
            }

            return Response(HttpStatusCode.OK, xml);
        }));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml",
            longEndpoint: "https://example.test/long-feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 18, 22, 14, 0, TimeSpan.FromHours(9)));

        EarthquakeReport report = Assert.Single(result.Reports);
        Assert.Equal("20260818221432_0_VXSE53_270000.xml", report.Source.SourceMessageId);
        Assert.Contains("Feed 2 条，命中 1 条", result.Status.Detail);

        EarthquakeSourceFetchResult incomplete = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 18, 22, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal(SourceConnectionState.Delayed, incomplete.Status.State);
        Assert.Contains("覆盖可能不足", incomplete.Status.Detail);
    }

    [Fact]
    public async Task FetchSince_UsesLongFeedWhenShortFeedDoesNotCoverSince()
    {
        const string shortFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260823231550_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/new.xml" />
              </entry>
            </feed>
            """;
        const string longFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260821143623_0_VXSE53_010000.xml</id>
                <link type="application/xml" href="https://example.test/old.xml" />
              </entry>
              <entry>
                <id>https://example.test/20260823231550_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/new.xml" />
              </entry>
            </feed>
            """;
        string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
              <Control><DateTime>2026-08-24T08:15:00+09:00</DateTime><Status>通常</Status><DateTime>2026-08-24T08:15:00+09:00</DateTime><EditorialOffice>気象庁本庁</EditorialOffice><PublishingOffice>気象庁</PublishingOffice><Title>地震情報</Title><EventID>20260824081215</EventID><ReportDateTime>2026-08-24T08:15:00+09:00</ReportDateTime><InfoType>発表</InfoType></Control>
              <Head><HeadlineText>地震情報</HeadlineText><InfoKind>地震情報</InfoKind><InfoKindVersion>1</InfoKindVersion></Head>
              <Body><Earthquake><OriginTime>2026-08-24T08:14:00+09:00</OriginTime><Hypocenter><Area><Name>相模湾</Name><Coordinate>35.0+139.0-10000/</Coordinate></Area></Hypocenter><Magnitude type="Mj">3.0</Magnitude></Earthquake><Comments><ForecastComment><Text>この地震による津波の心配はありません。</Text></ForecastComment></Comments></Body>
            </Report>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
        {
            if (uri.AbsoluteUri.EndsWith("long-feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, longFeed);
            }

            if (uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, shortFeed);
            }

            return Response(HttpStatusCode.OK, xml);
        }));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml",
            longEndpoint: "https://example.test/long-feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.FromHours(9)));

        Assert.Equal(2, result.Reports.Length);
        Assert.Contains("长期 Feed", result.Status.Detail);
        Assert.Contains("覆盖可能不足", result.Status.Detail);
    }

    [Fact]
    public async Task Fetch_MalformedFeed_ReturnsParseFailedStatus()
    {
        using var httpClient = new HttpClient(new RoutingResponseHandler(_ =>
            Response(HttpStatusCode.OK, "<feed>")));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.ParseFailed, result.Status.State);
        Assert.Contains("格式错误", result.Status.Detail);
    }

    [Fact]
    public async Task Fetch_ReportRateLimit_ReturnsRateLimitedStatus()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260818221432_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/report.xml" />
              </entry>
            </feed>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
            uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, feed)
                : Response(HttpStatusCode.TooManyRequests)));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.RateLimited, result.Status.State);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string? content = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (content is not null)
        {
            response.Content = new StringContent(content);
        }

        return response;
    }

    private sealed class RoutingResponseHandler(
        Func<Uri, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request.RequestUri!));
        }
    }
}
