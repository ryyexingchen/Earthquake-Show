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
