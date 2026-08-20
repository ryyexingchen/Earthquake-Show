using System.Net;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class P2pQuakeEarthquakeSourceTests
{
    [Fact]
    public async Task Fetch_DetailScale_MapsHypocenterAndStations()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "p2p-message-1",
              "issue": { "correct": "None", "source": "気象庁", "time": "2026/08/20 12:08:07", "type": "DetailScale" },
              "earthquake": {
                "domesticTsunami": "None",
                "foreignTsunami": "Unknown",
                "hypocenter": { "depth": 10, "latitude": 32.4, "longitude": 130.6, "magnitude": 2.9, "name": "熊本県熊本地方" },
                "maxScale": 40,
                "time": "2026/08/20 12:04:00"
              },
              "points": [
                { "addr": "八代市平山新町", "isArea": false, "pref": "熊本県", "scale": 30 }
              ],
              "time": "2026/08/20 12:08:08.078"
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        EarthquakeReport report = Assert.Single(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Equal("p2pquake:p2p-message-1", report.EventId);
        Assert.Equal("P2P-551", report.ReportCode);
        Assert.Equal(EarthquakeReportType.HypocenterAndIntensity, report.ReportType);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 12, 8, 7, TimeSpan.FromHours(9)), report.IssuedAt);
        Assert.Equal(new GeoCoordinate(32.4, 130.6), report.Hypocenter?.Coordinate);
        Assert.Equal(10, report.Hypocenter?.DepthKm);
        Assert.Equal(2.9, report.Magnitude?.Value);
        Assert.Equal(JmaIntensity.Four, report.MaxIntensity);
        IntensityStation station = Assert.Single(report.IntensityStations);
        Assert.Equal("八代市平山新町", station.Name);
        Assert.Equal(JmaIntensity.Three, station.Intensity);
        Assert.StartsWith("p2p:熊本県:", station.Code, StringComparison.Ordinal);
        Assert.Contains("\"p2p-message-1\"", report.Source.SourcePayload);
    }

    [Fact]
    public async Task Fetch_RateLimited_ReturnsRateLimitedStatus()
    {
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.TooManyRequests)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.RateLimited, result.Status.State);
    }

    [Fact]
    public async Task Fetch_MalformedPayload_ReturnsParseFailedStatus()
    {
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, "{\"invalid\":true}")));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeSourceFetchResult result = await source.FetchAsync();

        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.ParseFailed, result.Status.State);
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

    private sealed class ResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = response.Content is null
                    ? null
                    : new StringContent(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()),
            });
        }
    }
}
