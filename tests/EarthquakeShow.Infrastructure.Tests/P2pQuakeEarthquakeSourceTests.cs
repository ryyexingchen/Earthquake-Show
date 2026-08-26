using System.Net;
using System.Net.WebSockets;
using System.Text;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class P2pQuakeEarthquakeSourceTests
{
    [Fact]
    public async Task Fetch_RegionCatalog_MapsOfficialMunicipalityAndAreaCodes()
    {
        const string catalogJson = """
            {
              "prefectures": [{ "code": "43", "name": "熊本県" }],
              "areas": [{ "code": "741", "name": "熊本県熊本", "prefectureCode": "43", "prefectureName": "熊本県" }],
              "municipalities": [{ "code": "4320200", "name": "八代市", "prefectureCode": "43", "prefectureName": "熊本県", "areaCode": "741", "areaName": "熊本県熊本", "aliases": ["八代市"] }]
            }
            """;
        const string stationCatalogJson = """
            {
              "schemaVersion": 1,
              "stations": [{ "name": "八代市平山新町", "latitude": 32.48, "longitude": 130.61 }]
            }
            """;
        const string payload = """
            [{
              "code": 551, "id": "catalog-p2p", "issue": { "correct": "None", "time": "2026/08/20 12:08:07", "type": "DetailScale" },
              "earthquake": { "hypocenter": { "depth": 10, "latitude": 32.4, "longitude": 130.6, "magnitude": 2.9, "name": "熊本県熊本地方" }, "maxScale": 40, "time": "2026/08/20 12:04:00" },
              "points": [{ "addr": "熊本県八代市平山新町", "pref": "熊本県", "scale": 30 }], "time": "2026/08/20 12:08:08.078"
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake",
            JmaIntensityRegionCatalog.Load(catalogJson),
            JmaStationCoordinateCatalog.LoadJson(stationCatalogJson));

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        IntensityArea area = Assert.Single(report.IntensityAreas);
        IntensityMunicipality municipality = Assert.Single(report.IntensityMunicipalities);
        IntensityStation station = Assert.Single(report.IntensityStations);
        Assert.Equal("741", area.Code);
        Assert.Equal("4320200", municipality.Code);
        Assert.Equal(municipality.Code, station.MunicipalityCode);
        Assert.Equal(new GeoCoordinate(32.48, 130.61), station.Coordinate);
    }

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
        Assert.Equal("津波の心配なし", report.TsunamiComment);
        IntensityArea area = Assert.Single(report.IntensityAreas);
        Assert.Equal("熊本県", area.Name);
        Assert.Equal("p2p-pref:熊本県", area.PrefectureCode);
        Assert.Equal(JmaIntensity.Three, area.MaxIntensity);
        IntensityMunicipality municipality = Assert.Single(report.IntensityMunicipalities);
        Assert.Equal("八代市", municipality.Name);
        Assert.Equal(area.Code, municipality.AreaCode);
        IntensityStation station = Assert.Single(report.IntensityStations);
        Assert.Equal("八代市平山新町", station.Name);
        Assert.Equal(JmaIntensity.Three, station.Intensity);
        Assert.Equal(municipality.Code, station.MunicipalityCode);
        Assert.StartsWith("p2p:熊本県:", station.Code, StringComparison.Ordinal);
        Assert.Contains("\"p2p-message-1\"", report.Source.SourcePayload);
    }

    [Fact]
    public async Task Fetch_TsunamiStatuses_MapsDomesticAndForeignMeanings()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "p2p-tsunami-status",
              "issue": { "correct": "None", "time": "2026/08/20 12:08:07", "type": "DetailScale" },
              "earthquake": {
                "domesticTsunami": "Checking",
                "foreignTsunami": "WarningPacificWide",
                "hypocenter": { "depth": 10, "latitude": 32.4, "longitude": 130.6, "magnitude": 2.9, "name": "熊本県熊本地方" },
                "maxScale": 40,
                "time": "2026/08/20 12:04:00"
              },
              "points": []
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        Assert.Equal(
            "津波 调查中；海外：太平洋广域可能有海啸",
            report.TsunamiComment);
    }

    [Fact]
    public async Task Fetch_DomesticWarning_MapsToTsunamiForecast()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "p2p-tsunami-warning",
              "issue": { "correct": "None", "time": "2026/08/20 12:08:07", "type": "DetailScale" },
              "earthquake": {
                "domesticTsunami": "Warning",
                "foreignTsunami": "Unknown",
                "hypocenter": { "depth": 10, "latitude": 32.4, "longitude": 130.6, "magnitude": 2.9, "name": "熊本県熊本地方" },
                "maxScale": 40,
                "time": "2026/08/20 12:04:00"
              },
              "points": []
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        Assert.Equal("津波预报", report.TsunamiComment);
    }

    [Fact]
    public async Task Fetch_DomesticUnknown_MapsToInvestigating()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "p2p-tsunami-unknown",
              "issue": { "correct": "None", "time": "2026/08/20 12:08:07", "type": "DetailScale" },
              "earthquake": {
                "domesticTsunami": "Unknown",
                "foreignTsunami": "Unknown",
                "hypocenter": { "depth": 10, "latitude": 32.4, "longitude": 130.6, "magnitude": 2.9, "name": "熊本県熊本地方" },
                "maxScale": 40,
                "time": "2026/08/20 12:04:00"
              },
              "points": []
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        Assert.Equal("津波 调查中", report.TsunamiComment);
    }

    [Fact]
    public async Task Fetch_ForeignVolcanicEruption_NormalizesUnknownValues()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "foreign-volcano-1",
              "comments": { "freeFormComment": "アンバエ火山で大規模な噴火が発生しました。" },
              "issue": { "correct": "None", "source": "気象庁", "time": "2026/08/25 20:35:07", "type": "Foreign" },
              "earthquake": {
                "domesticTsunami": "Checking",
                "foreignTsunami": "Unknown",
                "hypocenter": { "depth": -1, "latitude": -15.4, "longitude": 167.8, "magnitude": -1, "name": "バヌアツ諸島" },
                "maxScale": -1,
                "time": "2026/08/25 18:40:00"
              },
              "points": [],
              "time": "2026/08/25 20:35:08.253"
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
        Assert.Equal(EarthquakeReportType.DistantEarthquake, report.ReportType);
        Assert.Equal(DistantEarthquakeKind.VolcanicEruption, report.DistantEarthquakeKind);
        Assert.Equal(new GeoCoordinate(-15.4, 167.8), report.Hypocenter?.Coordinate);
        Assert.Null(report.Hypocenter?.DepthKm);
        Assert.Null(report.Magnitude);
        Assert.Equal(JmaIntensity.Unknown, report.MaxIntensity);
    }

    [Fact]
    public async Task Fetch_ForeignEarthquake_PreservesKnownMagnitudeAndUnknownDepth()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "foreign-earthquake-1",
              "comments": { "freeFormComment": "南米西部で規模の大きな地震が発生しました。" },
              "issue": { "correct": "None", "source": "気象庁", "time": "2026/08/25 21:00:00", "type": "Foreign" },
              "earthquake": {
                "hypocenter": { "depth": -1, "latitude": -12.3, "longitude": -77.1, "magnitude": 7.2, "name": "南米西部" },
                "maxScale": -1,
                "time": "2026/08/25 20:30:00"
              },
              "points": []
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        Assert.Equal(EarthquakeReportType.DistantEarthquake, report.ReportType);
        Assert.Equal(DistantEarthquakeKind.Earthquake, report.DistantEarthquakeKind);
        Assert.Equal(new GeoCoordinate(-12.3, -77.1), report.Hypocenter?.Coordinate);
        Assert.Null(report.Hypocenter?.DepthKm);
        Assert.Equal(7.2, report.Magnitude?.Value);
    }

    [Fact]
    public async Task Fetch_InvalidSentinelCoordinate_DoesNotRejectWholeBatch()
    {
        const string catalogJson = """
            {
              "prefectures": [{ "code": "01", "name": "北海道" }],
              "areas": [{ "code": "013", "name": "釧路地方中南部", "prefectureCode": "01", "prefectureName": "北海道" }],
              "municipalities": []
            }
            """;
        const string payload = """
            [{
              "code": 551, "id": "scale-prompt-1",
              "issue": { "time": "2026/08/25 18:09:04", "type": "ScalePrompt" },
              "earthquake": {
                "hypocenter": { "depth": -1, "latitude": -200, "longitude": -200, "magnitude": -1, "name": "" },
                "maxScale": 30, "time": "2026/08/25 18:07:00"
              },
              "points": [{ "addr": "釧路地方中南部", "isArea": true, "pref": "北海道", "scale": 30 }]
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake",
            JmaIntensityRegionCatalog.Load(catalogJson));

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        Assert.Null(report.Hypocenter);
        Assert.Null(report.Magnitude);
        IntensityArea area = Assert.Single(report.IntensityAreas);
        Assert.Equal("013", area.Code);
        Assert.Equal("釧路地方中南部", area.Name);
        Assert.Equal("01", area.PrefectureCode);
        Assert.Equal("北海道", area.PrefectureName);
        Assert.Equal(JmaIntensity.Three, area.MaxIntensity);
        Assert.Empty(report.IntensityMunicipalities);
        Assert.Empty(report.IntensityStations);
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
    public async Task Fetch_ParsesTownMunicipalityPrefix()
    {
        const string payload = """
            [{
              "code": 551,
              "id": "p2p-town-1",
              "issue": { "time": "2026/08/20 12:08:07", "type": "DetailScale" },
              "earthquake": {
                "hypocenter": { "depth": 10, "latitude": 31.0, "longitude": 130.0, "magnitude": 2.0, "name": "鹿児島県" },
                "maxScale": 30,
                "time": "2026/08/20 12:04:00"
              },
              "points": [
                { "addr": "鹿児島県屋久島町宮之浦", "pref": "鹿児島県", "scale": 30 }
              ]
            }]
            """;
        using var httpClient = new HttpClient(new ResponseHandler(
            Response(HttpStatusCode.OK, payload)));
        var source = new P2pQuakeEarthquakeSource(
            httpClient,
            "https://example.test/v2/jma/quake");

        EarthquakeReport report = Assert.Single((await source.FetchAsync()).Reports);

        Assert.Equal("屋久島町", Assert.Single(report.IntensityMunicipalities).Name);
        Assert.Equal("屋久島町", Assert.Single(report.IntensityStations).MunicipalityCode.Split(':')[2]);
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

    [Fact]
    public async Task WebSocket_SingleTextMessage_MapsReport()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), P2pQuakeWebSocketSource.KeepAliveInterval);
        var configuredSource = new P2pQuakeWebSocketSource(
            "wss://example.test/v2/ws",
            TimeSpan.FromSeconds(60));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            configuredSource.ConfiguredKeepAliveInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new P2pQuakeWebSocketSource(
                "wss://example.test/v2/ws",
                TimeSpan.FromSeconds(5)));
        var connection = new FakeWebSocketConnection(
            Frame.Text(ValidObjectPayload));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        EarthquakeSourceFetchResult result = enumerator.Current;
        EarthquakeReport report = Assert.Single(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Equal(result.Status.CheckedAt, result.Status.LastMessageAt);
        Assert.Equal("p2pquake-ws", result.Status.SourceId);
        Assert.Equal("p2pquake:p2p-message-1", report.EventId);
        Assert.Equal("p2pquake", report.Source.SourceId);
        Assert.Equal("wss://example.test/v2/ws", report.Source.RawMessageUri?.ToString());
    }

    [Fact]
    public async Task WebSocket_RawMessageObserver_ReceivesCompleteTextPayload()
    {
        string? captured = null;
        string firstPart = ValidObjectPayload[..(ValidObjectPayload.Length / 2)];
        string secondPart = ValidObjectPayload[(ValidObjectPayload.Length / 2)..];
        var connection = new FakeWebSocketConnection(
            Frame.Text(firstPart, endOfMessage: false),
            Frame.Text(secondPart));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws",
            rawMessageObserver: payload => captured = payload);

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ValidObjectPayload, captured);
    }

    [Fact]
    public async Task WebSocket_ReassemblesFragments_AndPreservesMessageBoundaries()
    {
        const string secondPayload = """
            {"code":552,"id":"p2p-message-2","issue":{"correct":"None","time":"2026/08/20 12:09:07","type":"Hypocenter"},"earthquake":{"hypocenter":{"depth":20,"latitude":32.5,"longitude":130.7,"magnitude":3.1,"name":"熊本県熊本地方"},"maxScale":20,"time":"2026/08/20 12:05:00"}}
            """;
        string firstPart = ValidObjectPayload[..(ValidObjectPayload.Length / 2)];
        string secondPart = ValidObjectPayload[(ValidObjectPayload.Length / 2)..];
        var connection = new FakeWebSocketConnection(
            Frame.Text(firstPart, endOfMessage: false),
            Frame.Text(secondPart),
            Frame.Text(secondPayload));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("p2pquake:p2p-message-1", Assert.Single(enumerator.Current.Reports).EventId);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("p2pquake:p2p-message-2", Assert.Single(enumerator.Current.Reports).EventId);
    }

    [Fact]
    public async Task WebSocket_ArrayMessage_ReturnsParseFailed_AndContinues()
    {
        var connection = new FakeWebSocketConnection(
            Frame.Text("[{\"id\":\"not-an-object\"}]"),
            Frame.Text(ValidObjectPayload));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(SourceConnectionState.ParseFailed, enumerator.Current.Status.State);
        Assert.Equal(
            enumerator.Current.Status.CheckedAt,
            enumerator.Current.Status.LastMessageAt);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(SourceConnectionState.Online, enumerator.Current.Status.State);
    }

    [Fact]
    public async Task WebSocket_NonEventMessage_IsIgnored_AndContinues()
    {
        var connection = new FakeWebSocketConnection(
            Frame.Text("{\"code\":999,\"time\":\"2026/08/20 12:09:00\"}"),
            Frame.Text(ValidObjectPayload));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        EarthquakeSourceFetchResult ignored = enumerator.Current;
        Assert.Empty(ignored.Reports);
        Assert.Equal(SourceConnectionState.Online, ignored.Status.State);
        Assert.Equal("P2PQuake WebSocket：忽略非事件消息", ignored.Status.Detail);
        Assert.Equal(ignored.Status.CheckedAt, ignored.Status.LastMessageAt);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("p2pquake:p2p-message-1", Assert.Single(enumerator.Current.Reports).EventId);
    }

    [Fact]
    public async Task WebSocket_Code555PeerStatistics_IsOnlineWithoutReport()
    {
        const string payload =
            "{\"_id\":\"peer-stats-1\",\"areas\":[{\"id\":250,\"peer\":290}]," +
            "\"code\":555,\"time\":\"2026/08/20 18:05:32.766\",\"ver\":\"20150406\"}";
        var connection = new FakeWebSocketConnection(Frame.Text(payload));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        EarthquakeSourceFetchResult result = enumerator.Current;
        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.Online, result.Status.State);
        Assert.Equal("P2PQuake WebSocket：网络节点统计（code 555）", result.Status.Detail);
        Assert.Equal(result.Status.CheckedAt, result.Status.LastMessageAt);
        Assert.Null(result.Status.ConnectionExceptionCount);
    }

    [Fact]
    public async Task WebSocket_EventShapeWithoutId_RemainsParseFailed()
    {
        const string payload =
            "{\"code\":551,\"issue\":{\"time\":\"2026/08/20 12:08:07\",\"type\":\"DetailScale\"}," +
            "\"earthquake\":{\"hypocenter\":{\"depth\":10,\"latitude\":32.4,\"longitude\":130.6}}}";
        var connection = new FakeWebSocketConnection(Frame.Text(payload));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Empty(enumerator.Current.Reports);
        Assert.Equal(SourceConnectionState.ParseFailed, enumerator.Current.Status.State);
        Assert.Contains("缺少 id", enumerator.Current.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_Handshake429_ReturnsRateLimitedWithoutConnectionException()
    {
        var connection = new FakeWebSocketConnection(
            connectException: new WebSocketException(
                "The server returned status code '429' when status code '101' was expected."));
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        EarthquakeSourceFetchResult result = enumerator.Current;
        Assert.Empty(result.Reports);
        Assert.Equal(SourceConnectionState.RateLimited, result.Status.State);
        Assert.Null(result.Status.ConnectionExceptionCount);
    }

    [Fact]
    public async Task WebSocket_Cancellation_IsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new FakeWebSocketConnection(blockOnReceive: true);
        var source = new P2pQuakeWebSocketSource(
            () => connection,
            "wss://example.test/v2/ws");
        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
    }

    private const string ValidObjectPayload = """
        {"code":551,"id":"p2p-message-1","issue":{"correct":"None","source":"気象庁","time":"2026/08/20 12:08:07","type":"DetailScale"},"earthquake":{"domesticTsunami":"None","foreignTsunami":"Unknown","hypocenter":{"depth":10,"latitude":32.4,"longitude":130.6,"magnitude":2.9,"name":"熊本県熊本地方"},"maxScale":40,"time":"2026/08/20 12:04:00"},"points":[{"addr":"八代市平山新町","isArea":false,"pref":"熊本県","scale":30}],"time":"2026/08/20 12:08:08.078"}
        """;

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

    private sealed record Frame(
        WebSocketMessageType MessageType,
        string Payload,
        bool EndOfMessage)
    {
        public static Frame Text(string payload, bool endOfMessage = true) =>
            new(WebSocketMessageType.Text, payload, endOfMessage);
    }

    private sealed class FakeWebSocketConnection : IWebSocketConnection
    {
        private readonly Queue<Frame> _frames;
        private readonly bool _blockOnReceive;
        private readonly Exception? _connectException;

        public FakeWebSocketConnection(params Frame[] frames)
        {
            _frames = new Queue<Frame>(frames);
        }

        public FakeWebSocketConnection(Exception connectException, params Frame[] frames)
        {
            _frames = new Queue<Frame>(frames);
            _connectException = connectException;
        }

        public FakeWebSocketConnection(bool blockOnReceive)
        {
            _frames = new Queue<Frame>();
            _blockOnReceive = blockOnReceive;
        }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            return _connectException is null
                ? Task.CompletedTask
                : Task.FromException(_connectException);
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_blockOnReceive)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_frames.Count == 0)
            {
                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true,
                    WebSocketCloseStatus.NormalClosure,
                    "测试结束");
            }

            Frame frame = _frames.Dequeue();
            byte[] bytes = Encoding.UTF8.GetBytes(frame.Payload);
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(
                bytes.Length,
                frame.MessageType,
                frame.EndOfMessage);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
