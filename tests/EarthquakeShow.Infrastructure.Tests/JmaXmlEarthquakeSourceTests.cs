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
    public async Task Fetch_Timeout_ReturnsDisconnectedStatus()
    {
        using var httpClient = new HttpClient(new RoutingResponseHandler(_ =>
            throw new TaskCanceledException("模拟请求超时")));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.Disconnected, result.Status.State);
        Assert.Contains("超时", result.Status.Detail);
    }

    [Fact]
    public async Task Fetch_FeedLinkWithoutXmlType_StillParsesSupportedEntry()
    {
        const string reportUri = "https://example.test/20260818221432_0_VXSE53_270000.xml";
        const string feed = $"""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>{reportUri}</id>
                <link href="{reportUri}" />
              </entry>
            </feed>
            """;
        const string xml = """
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/"><Control><DateTime>2026-08-18T22:14:32+09:00</DateTime><Status>通常</Status></Control><Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/"><ReportDateTime>2026-08-18T22:14:32+09:00</ReportDateTime><EventID>20260818221432</EventID><InfoType>発表</InfoType></Head><Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/seismology1/"><Earthquake><OriginTime>2026-08-18T22:14:00+09:00</OriginTime><Hypocenter><Area><Name>相模湾</Name><Coordinate>35.0+139.0-10000/</Coordinate></Area></Hypocenter><Magnitude type="Mj">3.0</Magnitude></Earthquake></Body></Report>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
            uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, feed)
                : Response(HttpStatusCode.OK, xml)));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Single(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
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
                <id>https://example.test/20260818131317_0_VXSE52_270000.xml</id>
                <link type="application/xml" href="https://example.test/old.xml" />
              </entry>
              <entry>
                <id>https://example.test/20260818131432_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/new.xml" />
              </entry>
            </feed>
            """;
        const string longFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260818131317_0_VXSE52_270000.xml</id>
                <link type="application/xml" href="https://example.test/old.xml" />
              </entry>
              <entry>
                <id>https://example.test/20260818131432_0_VXSE53_270000.xml</id>
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
        Assert.Equal("20260818131432_0_VXSE53_270000.xml", report.Source.SourceMessageId);
        Assert.Contains("Feed 2 条，命中 1 条", result.Status.Detail);

        EarthquakeSourceFetchResult incomplete = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 18, 22, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal(SourceConnectionState.Delayed, incomplete.Status.State);
        Assert.Contains("覆盖可能不足", incomplete.Status.Detail);
    }

    [Fact]
    public async Task FetchSince_DoesNotTruncateEntriesBeforeIncrementalFilter()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><id>https://example.test/20260824125441_0_VXSE53_010000.xml</id><link type="application/xml" href="https://example.test/report.xml" /></entry>
              <entry><id>https://example.test/20260824125341_0_VXSE53_010000.xml</id><link type="application/xml" href="https://example.test/report.xml" /></entry>
              <entry><id>https://example.test/20260824125241_0_VXSE53_010000.xml</id><link type="application/xml" href="https://example.test/report.xml" /></entry>
            </feed>
            """;
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
              <Control><DateTime>2026-08-24T12:55:00+09:00</DateTime><Status>通常</Status><EditorialOffice>気象庁本庁</EditorialOffice><PublishingOffice>気象庁</PublishingOffice><Title>地震情報</Title><EventID>20260824125441</EventID><ReportDateTime>2026-08-24T12:55:00+09:00</ReportDateTime><InfoType>発表</InfoType></Control>
              <Head><HeadlineText>地震情報</HeadlineText><InfoKind>地震情報</InfoKind><InfoKindVersion>1</InfoKindVersion></Head>
              <Body><Earthquake><OriginTime>2026-08-24T12:54:00+09:00</OriginTime><Hypocenter><Area><Name>釧路地方中南部</Name><Coordinate>43.2+145.0-80000/</Coordinate></Area></Hypocenter><Magnitude type="Mj">3.3</Magnitude></Earthquake></Body>
            </Report>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
            uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, feed)
                : Response(HttpStatusCode.OK, xml)));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml",
            maxEntries: 1);

        EarthquakeSourceFetchResult result = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9)));

        Assert.Equal(3, result.Reports.Length);
        Assert.Contains(result.Reports, report =>
            report.Source.SourceMessageId.StartsWith("20260824125441", StringComparison.Ordinal));
        Assert.Contains("命中 3 条", result.Status.Detail);
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
    public async Task FetchSince_EmptyShortFeed_UsesLongFeed()
    {
        const string longFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>https://example.test/20260824120000_0_VXSE53_270000.xml</id>
                <link type="application/xml" href="https://example.test/report.xml" />
              </entry>
            </feed>
            """;
        const string xml = """
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/"><Control><DateTime>2026-08-24T12:00:00+09:00</DateTime><Status>通常</Status></Control><Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/"><ReportDateTime>2026-08-24T12:00:00+09:00</ReportDateTime><EventID>20260824115900</EventID><InfoType>発表</InfoType></Head><Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/seismology1/"><Earthquake><OriginTime>2026-08-24T11:59:00+09:00</OriginTime><Hypocenter><Area><Name>相模湾</Name><Coordinate>35.0+139.0-10000/</Coordinate></Area></Hypocenter><Magnitude type="Mj">3.0</Magnitude></Earthquake></Body></Report>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
        {
            if (uri.AbsoluteUri.EndsWith("long-feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, longFeed);
            }

            if (uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, "<feed xmlns=\"http://www.w3.org/2005/Atom\" />");
            }

            return Response(HttpStatusCode.OK, xml);
        }));
        var source = new JmaXmlEarthquakeSource(
            httpClient,
            endpoint: "https://example.test/feed.xml",
            longEndpoint: "https://example.test/long-feed.xml");

        EarthquakeSourceFetchResult result = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.FromHours(9)));

        Assert.Single(result.Reports);
        Assert.Contains("长期 Feed", result.Status.Detail);
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
