using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class NtoolYahooRealtimeObservationSourceTests
{
    [Fact]
    public void ParseSiteList_MapsAliasesAndDropsInvalidCoordinates()
    {
        const string payload = """
            {
              "sites": [
                { "id": "A", "name": "甲站", "lat": 35.1, "lon": 139.1 },
                { "code": "B", "stationName": "乙站", "latitude": "36.2", "longitude": "140.2" },
                { "id": "bad", "lat": 95, "lon": 140 }
              ]
            }
            """;

        var sites = NtoolYahooRealtimeObservationSource.ParseSiteList(payload);

        Assert.Equal(2, sites.Length);
        Assert.Equal("A", sites[0].Code);
        Assert.Equal("甲站", sites[0].Name);
        Assert.Equal(new GeoCoordinate(35.1, 139.1), sites[0].Coordinate);
        Assert.Equal("B", sites[1].Code);
        Assert.Equal(new GeoCoordinate(36.2, 140.2), sites[1].Coordinate);
    }

    [Fact]
    public void ParseSiteCatalog_MapsYahooItemsAndConfigId()
    {
        const string payload = """
            { "siteConfigId": "cfg-1", "items": [[35, 139], [36.2, "140.2"]] }
            """;

        var catalog = NtoolYahooRealtimeObservationSource.ParseSiteCatalog(payload);

        Assert.Equal("cfg-1", catalog.SiteConfigId);
        Assert.Equal(2, catalog.Sites.Length);
        Assert.Equal("site-index:0", catalog.Sites[0].Code);
        Assert.Equal(0, catalog.Sites[0].Index);
        Assert.Equal(new GeoCoordinate(36.2, 140.2), catalog.Sites[1].Coordinate);
    }

    [Fact]
    public void ParseRealtimeData_MapsYahooIntensityCharacters()
    {
        const string sitePayload = """
            { "siteConfigId": "cfg-1", "items": [[35, 139], [36, 140], [37, 141], [38, 142]] }
            """;
        var catalog = NtoolYahooRealtimeObservationSource.ParseSiteCatalog(sitePayload);
        const string dataPayload = """
            { "realTimeData": { "siteConfigId": "cfg-1", "dataTime": "20260901120000", "intensity": "dnrt" } }
            """;
        DateTimeOffset sampledAt = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);

        var stations = NtoolYahooRealtimeObservationSource.ParseRealtimeData(
            dataPayload,
            catalog,
            sampledAt,
            sampledAt.AddSeconds(1));

        Assert.Equal(4, stations.Length);
        Assert.True(stations[0].IsZero);
        Assert.Equal(JmaIntensity.Unknown, stations[0].Intensity);
        Assert.Equal(JmaIntensity.Two, stations[1].Intensity);
        Assert.Equal(JmaIntensity.Four, stations[2].Intensity);
        Assert.Equal(JmaIntensity.FiveLower, stations[3].Intensity);
        Assert.All(stations, station =>
            Assert.Equal(RealtimeObservationQuality.Valid, station.Quality));
    }

    [Fact]
    public void ParseRealtimeData_RejectsMismatchedConfigAndLength()
    {
        var catalog = NtoolYahooRealtimeObservationSource.ParseSiteCatalog(
            "{\"siteConfigId\":\"cfg-1\",\"items\":[[35,139],[36,140]]}");

        Assert.Throws<FormatException>(() =>
            NtoolYahooRealtimeObservationSource.ParseRealtimeData(
                "{\"realTimeData\":{\"siteConfigId\":\"cfg-2\",\"intensity\":\"dn\"}}",
                catalog,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        Assert.Throws<FormatException>(() =>
            NtoolYahooRealtimeObservationSource.ParseRealtimeData(
                "{\"realTimeData\":{\"siteConfigId\":\"cfg-1\",\"intensity\":\"d\"}}",
                catalog,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task FetchAsync_UsesYahooTimestampAndReadsGzipPayload()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/sitelist.json", StringComparison.Ordinal))
            {
                return JsonResponse("{\"siteConfigId\":\"cfg-1\",\"items\":[[35,139]]}");
            }

            Assert.EndsWith("/RealTimeData/20260901/20260901120000.json", request.RequestUri.AbsolutePath);
            return GzipJsonResponse(
                "{\"realTimeData\":{\"siteConfigId\":\"cfg-1\",\"intensity\":\"t\"}}");
        }));
        var source = new NtoolYahooRealtimeObservationSource(
            client,
            "https://example.test/SiteList/sitelist.json",
            "https://example.test/RealTimeData",
            () => new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero));

        RealtimeObservationFetchResult result = await source.FetchAsync();

        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Single(result.Stations);
        Assert.Equal(JmaIntensity.FiveLower, result.Stations[0].Intensity);
        Assert.Contains(requests, uri => uri.Query.Contains("time=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_FallsBackToEarlierSecondAsDelayed()
    {
        var dataRequests = new List<Uri>();
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sitelist.json", StringComparison.Ordinal))
            {
                return JsonResponse("{\"siteConfigId\":\"cfg-1\",\"items\":[[35,139]]}");
            }

            dataRequests.Add(request.RequestUri);
            if (request.RequestUri.AbsolutePath.EndsWith("20260901115958.json", StringComparison.Ordinal))
            {
                return JsonResponse(
                    "{\"realTimeData\":{\"siteConfigId\":\"cfg-1\",\"intensity\":\"n\"}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var source = new NtoolYahooRealtimeObservationSource(
            client,
            "https://example.test/SiteList/sitelist.json",
            "https://example.test/RealTimeData",
            () => new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero));

        RealtimeObservationFetchResult result = await source.FetchAsync();

        Assert.Equal(SourceConnectionState.Delayed, result.Status.State);
        Assert.Single(result.Stations);
        Assert.Equal(JmaIntensity.Two, result.Stations[0].Intensity);
        Assert.Equal(3, dataRequests.Count);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage GzipJsonResponse(string json)
    {
        using var source = new MemoryStream();
        using (var gzip = new GZipStream(source, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            writer.Write(json);
        }

        var content = new ByteArrayContent(source.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
