using System.Net;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class JmaTsunamiXmlSourceTests
{
    [Fact]
    public async Task Fetch_FiltersVtseEntries_AndPreservesSourceIdentity()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><id>https://example.test/20260824195900_0_VXSE53_010000.xml</id><link type="application/xml" href="https://example.test/earthquake.xml" /></entry>
              <entry><id>https://example.test/20260824200000_0_VTSE41_010000.xml</id><link type="application/xml" href="https://example.test/tsunami.xml" /></entry>
            </feed>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
            uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, feed)
                : Response(HttpStatusCode.OK, ReportXml())));
        var source = new JmaTsunamiXmlSource(httpClient, endpoint: "https://example.test/feed.xml");

        TsunamiSourceFetchResult result = await source.FetchAsync();

        JmaTsunamiReport report = Assert.Single(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Equal("VTSE41", report.ReportCode);
        Assert.Equal("20260824200000_0_VTSE41_010000.xml", report.Source.SourceMessageId);
        Assert.Equal("https://example.test/tsunami.xml", report.Source.RawMessageUri!.AbsoluteUri);
        Assert.Contains("<Report", report.Source.SourcePayload);
    }

    [Fact]
    public async Task Fetch_ReportFailure_ReturnsDisconnectedStatus()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><id>https://example.test/20260824200000_0_VTSE41_010000.xml</id><link type="application/xml" href="https://example.test/tsunami.xml" /></entry>
            </feed>
            """;
        using var httpClient = new HttpClient(new RoutingResponseHandler(uri =>
            uri.AbsoluteUri.EndsWith("feed.xml", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, feed)
                : Response(HttpStatusCode.InternalServerError)));
        var source = new JmaTsunamiXmlSource(httpClient, endpoint: "https://example.test/feed.xml");

        TsunamiSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.Disconnected, result.Status.State);
        Assert.Contains("失败 1 条", result.Status.Detail);
    }

    [Fact]
    public async Task FetchSince_UsesLongFeedWhenShortFeedDoesNotCoverSince()
    {
        const string shortFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><id>https://example.test/20260824200000_0_VTSE41_010000.xml</id><link type="application/xml" href="https://example.test/new.xml" /></entry>
            </feed>
            """;
        const string longFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><id>https://example.test/20260821200000_0_VTSE51_010000.xml</id><link type="application/xml" href="https://example.test/old.xml" /></entry>
              <entry><id>https://example.test/20260824200000_0_VTSE41_010000.xml</id><link type="application/xml" href="https://example.test/new.xml" /></entry>
            </feed>
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

            return Response(HttpStatusCode.OK, ReportXml());
        }));
        var source = new JmaTsunamiXmlSource(
            httpClient,
            endpoint: "https://example.test/feed.xml",
            longEndpoint: "https://example.test/long-feed.xml");

        TsunamiSourceFetchResult result = await source.FetchSinceAsync(
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.FromHours(9)));

        Assert.Equal(2, result.Reports.Length);
        Assert.Contains("长期 Feed", result.Status.Detail);
    }

    private static string ReportXml() => """
        <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
          <Control><DateTime>2026-08-24T20:00:00+09:00</DateTime><Status>通常</Status></Control>
          <Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/">
            <ReportDateTime>2026-08-24T20:00:00+09:00</ReportDateTime><EventID>tsunami-event</EventID>
            <InfoType>発表</InfoType><Serial>1</Serial><InfoKind>津波警報・注意報・予報</InfoKind>
            <Headline><Text>津波注意報を発表しています。</Text><Information><Item>
              <Kind><Name>津波注意報</Name><Code>advisory-code</Code></Kind>
              <Areas><Area><Name>茨城県</Name><Code>301</Code></Area></Areas>
            </Item></Information></Headline>
          </Head>
          <Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/tsunami1/" />
        </Report>
        """;

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string? content = null)
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
