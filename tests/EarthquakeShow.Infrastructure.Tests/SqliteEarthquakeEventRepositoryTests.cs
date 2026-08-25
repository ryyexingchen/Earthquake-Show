using System.Collections.Immutable;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Persistence;
using EarthquakeShow.Infrastructure.Sources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class SqliteEarthquakeEventRepositoryTests
{
    [Fact]
    public async Task Initialize_EmptyDatabase_WritesSeedAndReloadsMergedEvent()
    {
        using var database = new TemporaryDatabase();
        ImmutableArray<EarthquakeReport> reports = LoadReports();

        var first = new SqliteEarthquakeEventRepository(database.Path);
        await first.InitializeAsync(reports);
        EarthquakeEvent firstEvent = Assert.Single(await first.ListEventsAsync());
        Assert.Equal(4, firstEvent.Reports.Length);
        Assert.Contains("已写入固定样本", first.CacheStatus);

        var second = new SqliteEarthquakeEventRepository(database.Path);
        await second.InitializeAsync([]);
        EarthquakeEvent cachedEvent = Assert.Single(await second.ListEventsAsync());
        Assert.Equal(firstEvent.EventId, cachedEvent.EventId);
        Assert.Equal(
            firstEvent.Reports.Select(report => report.Source.SourceMessageId),
            cachedEvent.Reports.Select(report => report.Source.SourceMessageId));
        Assert.Equal(firstEvent.Summary, cachedEvent.Summary);
        Assert.Contains("已读取 4 条报文", second.CacheStatus);
    }

    [Fact]
    public async Task Initialize_MigratesAndRemovesHistoricalJmaJsonReports()
    {
        using var database = new TemporaryDatabase();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO schema_info VALUES ('schema_version', '2');
                CREATE TABLE earthquake_reports (
                    event_id TEXT NOT NULL, source_id TEXT NOT NULL,
                    source_message_id TEXT NOT NULL, report_code TEXT NOT NULL,
                    issued_at TEXT NOT NULL, received_at TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    PRIMARY KEY (event_id, source_id, source_message_id));
                CREATE TABLE earthquake_events (
                    event_id TEXT PRIMARY KEY, updated_at TEXT NOT NULL,
                    report_count INTEGER NOT NULL);
                CREATE TABLE source_status (
                    source_id TEXT PRIMARY KEY, state TEXT NOT NULL,
                    checked_at TEXT NOT NULL, last_received_at TEXT NULL,
                    detail TEXT NULL);
                INSERT INTO earthquake_reports VALUES (
                    'legacy-event', 'jma-json', 'legacy-message', 'JMA-JSON',
                    '2026-08-20T00:00:00.0000000+09:00',
                    '2026-08-20T00:00:01.0000000+09:00',
                    '{"eventId":"legacy-event","reportCode":"JMA-JSON","reportType":"HypocenterAndIntensity","status":"Issued","context":"Normal","serial":1,"originTime":"2026-08-20T00:00:00.0000000+09:00","issuedAt":"2026-08-20T00:00:00.0000000+09:00","receivedAt":"2026-08-20T00:00:01.0000000+09:00","maxIntensity":"four","intensityAreas":[],"intensityMunicipalities":[],"intensityStations":[],"source":{"sourceId":"jma-json","sourceMessageId":"legacy-message"}}');
                INSERT INTO source_status VALUES (
                    'jma-json', 'Disabled',
                    '2026-08-20T00:00:01.0000000+09:00', NULL, '旧来源');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteEarthquakeEventRepository(database.Path);
        await repository.InitializeAsync([]);

        Assert.DoesNotContain(
            repository.SourceStatuses,
            status => status.SourceId == "jma-json");
        Assert.DoesNotContain(
            (await repository.ListEventsAsync()).SelectMany(item => item.Reports),
            report => report.Source.SourceId == "jma-json");
        await using var verify = new SqliteConnection(builder.ConnectionString);
        await verify.OpenAsync();
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM earthquake_events WHERE event_id = 'legacy-event';";
        Assert.Equal(0L, Convert.ToInt64(await verifyCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Initialize_ExistingDatabase_AddsMissingSeedReportsWithoutDuplicates()
    {
        using var database = new TemporaryDatabase();
        EarthquakeReport cachedReport = CreateOnlineReport();
        ImmutableArray<EarthquakeReport> seedReports = LoadReports();

        var first = new SqliteEarthquakeEventRepository(database.Path);
        await first.InitializeAsync([cachedReport]);

        var second = new SqliteEarthquakeEventRepository(database.Path);
        await second.InitializeAsync(seedReports);

        ImmutableArray<EarthquakeEvent> events = await second.ListEventsAsync();
        Assert.Equal(2, events.Length);
        EarthquakeEvent sampleEvent = Assert.Single(
            events,
            item => item.EventId == seedReports[0].EventId);
        Assert.Equal(4, sampleEvent.Reports.Length);
        Assert.Contains("补充固定样本 4 条报文", second.CacheStatus);

        var third = new SqliteEarthquakeEventRepository(database.Path);
        await third.InitializeAsync(seedReports);
        Assert.Equal(2, (await third.ListEventsAsync()).Length);
        Assert.Contains("已读取 5 条报文", third.CacheStatus);
    }

    [Fact]
    public async Task Initialize_ReadOnlyCache_PreservesLoadedEventsWhenStatusWriteFails()
    {
        using var database = new TemporaryDatabase();
        ImmutableArray<EarthquakeReport> seedReports = LoadReports();
        var writer = new SqliteEarthquakeEventRepository(database.Path);
        await writer.InitializeAsync(seedReports.Append(CreateOnlineReport()));
        File.SetAttributes(database.Path, FileAttributes.ReadOnly);

        try
        {
            var reader = new SqliteEarthquakeEventRepository(database.Path);
            await reader.InitializeAsync(seedReports);

            Assert.Equal(2, (await reader.ListEventsAsync()).Length);
            Assert.Contains("只读模式，已读取 5 条报文", reader.CacheStatus);
        }
        finally
        {
            File.SetAttributes(database.Path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Initialize_PreservesRawPayloadSourceAndCorrectionFields()
    {
        using var database = new TemporaryDatabase();
        var repository = new SqliteEarthquakeEventRepository(database.Path);

        await repository.InitializeAsync(LoadReports());

        EarthquakeReport correction = Assert.Single(
            Assert.Single(await repository.ListEventsAsync()).Reports,
            report => report.Status == ReportStatus.Correction);
        Assert.Equal("jma-xml", correction.Source.SourceId);
        Assert.Equal("vxse53-correction.xml", correction.Source.SourceMessageId);
        Assert.Contains("<", correction.Source.SourcePayload);
        Assert.Equal(2, correction.Serial);
        Assert.Equal(3.9, correction.Magnitude?.Value);
        Assert.Equal(75, correction.IntensityStations.Length);
        Assert.All(correction.IntensityStations, station => Assert.NotNull(station.Coordinate));
    }

    [Fact]
    public async Task Initialize_OldCachedJmaXml_FillsOnlyMissingJmaCoordinates()
    {
        using var database = new TemporaryDatabase();
        EarthquakeReport jmaReport = CreateOnlineReport() with
        {
            Source = new SourceReference("jma-xml", "cached-jma-xml"),
            IntensityStations =
            [
                new IntensityStation(
                    string.Empty,
                    "水戸市栗崎町＊",
                    string.Empty,
                    JmaIntensity.Two,
                    null),
                new IntensityStation(
                    "known-station",
                    "既知観測点",
                    string.Empty,
                    JmaIntensity.Three,
                    new GeoCoordinate(35.1, 139.2)),
            ],
        };
        EarthquakeReport p2pReport = CreateOnlineReport() with
        {
            Source = new SourceReference("p2pquake", "cached-p2pquake"),
            IntensityStations =
            [
                new IntensityStation(
                    string.Empty,
                    "水戸市栗崎町＊",
                    string.Empty,
                    JmaIntensity.Two,
                    null),
            ],
        };
        var writer = new SqliteEarthquakeEventRepository(database.Path);
        await writer.InitializeAsync([jmaReport, p2pReport]);
        const string catalogJson = """
            {
              "schemaVersion": 1,
              "stations": [
                { "name": "水戸市栗崎町", "latitude": 36.31, "longitude": 140.49 }
              ]
            }
            """;
        JmaStationCoordinateCatalog catalog = JmaStationCoordinateCatalog.LoadJson(catalogJson);

        var reader = new SqliteEarthquakeEventRepository(
            database.Path,
            stationCatalog: catalog);
        await reader.InitializeAsync([]);

        EarthquakeEvent cachedEvent = Assert.Single(await reader.ListEventsAsync());
        EarthquakeReport cachedJma = Assert.Single(
            cachedEvent.Reports,
            report => report.Source.SourceId == "jma-xml");
        Assert.Equal(
            new GeoCoordinate(36.31, 140.49),
            cachedJma.IntensityStations[0].Coordinate);
        Assert.Equal(
            new GeoCoordinate(35.1, 139.2),
            cachedJma.IntensityStations[1].Coordinate);
        EarthquakeReport cachedP2p = Assert.Single(
            cachedEvent.Reports,
            report => report.Source.SourceId == "p2pquake");
        Assert.Null(Assert.Single(cachedP2p.IntensityStations).Coordinate);
    }

    [Fact]
    public async Task Initialize_LegacyJmaAsciiIntensity_ReparsesRawXml()
    {
        using var database = new TemporaryDatabase();
        const string xml = """
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
              <Control><DateTime>2026-08-23T00:00:00Z</DateTime><Status>通常</Status></Control>
              <Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/"><ReportDateTime>2026-08-23T09:00:00+09:00</ReportDateTime><EventID>legacy-intensity</EventID><InfoType>発表</InfoType></Head>
              <Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/seismology1/"><Intensity><Observation><MaxInt>5-</MaxInt><Pref><Name>茨城県</Name><Code>08</Code><MaxInt>5-</MaxInt><Area><Name>茨城県南部</Name><Code>301</Code><MaxInt>5-</MaxInt><City><Name>小美玉市</Name><Code>0823600</Code><MaxInt>5-</MaxInt><IntensityStation><Name>小美玉市上玉里＊</Name><Code>0823635</Code><Int>5-</Int></IntensityStation></City></Area></Pref></Observation></Intensity></Body>
            </Report>
            """;
        EarthquakeReport parsed = JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions("VXSE53", new SourceReference("jma-xml", "legacy-intensity")));
        EarthquakeReport legacy = parsed with { MaxIntensity = JmaIntensity.Unknown };

        var writer = new SqliteEarthquakeEventRepository(database.Path);
        await writer.InitializeAsync([legacy]);
        var reader = new SqliteEarthquakeEventRepository(database.Path);
        await reader.InitializeAsync([]);

        EarthquakeReport reloaded = Assert.Single(
            Assert.Single(await reader.ListEventsAsync()).Reports);
        Assert.Equal(JmaIntensity.FiveLower, reloaded.MaxIntensity);
        Assert.Equal(JmaIntensity.FiveLower, Assert.Single(reloaded.IntensityStations).Intensity);
    }

    [Fact]
    public async Task Initialize_PersistsOfflineSourceStatus()
    {
        using var database = new TemporaryDatabase();
        var first = new SqliteEarthquakeEventRepository(database.Path);
        await first.InitializeAsync(LoadReports());

        SourceStatus status = Assert.Single(first.SourceStatuses);
        Assert.Equal("jma-xml", status.SourceId);
        Assert.Equal(SourceConnectionState.Disabled, status.State);
        Assert.Equal("离线缓存，尚未连接实时数据源", status.Detail);

        var second = new SqliteEarthquakeEventRepository(database.Path);
        await second.InitializeAsync([]);
        Assert.Equal(status.SourceId, Assert.Single(second.SourceStatuses).SourceId);
        Assert.Equal(SourceConnectionState.Disabled, Assert.Single(second.SourceStatuses).State);
    }

    [Fact]
    public async Task Initialize_CorruptDatabase_FallsBackToSeedWithoutThrowing()
    {
        using var database = new TemporaryDatabase();
        await File.WriteAllTextAsync(database.Path, "this is not a sqlite database");
        var repository = new SqliteEarthquakeEventRepository(database.Path);

        await repository.InitializeAsync(LoadReports());

        Assert.Single(await repository.ListEventsAsync());
        Assert.Contains("回退固定样本", repository.CacheStatus);
    }

    [Fact]
    public async Task Initialize_UnsupportedSchema_PreservesSnapshotAndAddsSeed()
    {
        using var database = new TemporaryDatabase();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Pooling = false,
        };
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO schema_info VALUES ('schema_version', '99');";
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteEarthquakeEventRepository(database.Path);
        await repository.InitializeAsync(LoadReports());

        Assert.Single(await repository.ListEventsAsync());
        Assert.Contains("只读模式，已读取 4 条报文", repository.CacheStatus);
    }

    [Fact]
    public async Task Initialize_CancelledToken_IsPropagated()
    {
        using var database = new TemporaryDatabase();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = new SqliteEarthquakeEventRepository(database.Path);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await repository.InitializeAsync(LoadReports(), cancellation.Token));
    }

    [Fact]
    public async Task Refresh_OnlineSource_PersistsAndPublishesNewEvent()
    {
        using var database = new TemporaryDatabase();
        EarthquakeReport onlineReport = CreateOnlineReport();
        var source = new StubRealtimeSource(new EarthquakeSourceFetchResult(
            [onlineReport],
            new SourceStatus(
                "p2pquake",
                SourceConnectionState.Online,
                onlineReport.ReceivedAt,
                onlineReport.ReceivedAt,
                "测试在线源")));
        var repository = new SqliteEarthquakeEventRepository(database.Path, source);
        await repository.InitializeAsync(LoadReports());

        await repository.RefreshAsync();

        Assert.Equal(2, (await repository.ListEventsAsync()).Length);
        Assert.Equal(
            SourceConnectionState.Online,
            Assert.Single(repository.SourceStatuses, status => status.SourceId == "p2pquake").State);
        Assert.Contains("实时源已更新 1 条报文", repository.CacheStatus);

        var reloaded = new SqliteEarthquakeEventRepository(database.Path);
        await reloaded.InitializeAsync([]);
        Assert.Equal(2, (await reloaded.ListEventsAsync()).Length);
    }

    [Fact]
    public async Task Refresh_IncrementalSource_ReceivesLatestCachedIssuedAt()
    {
        using var database = new TemporaryDatabase();
        var source = new StubIncrementalSource(
            new EarthquakeSourceFetchResult(
                [],
                new SourceStatus(
                    "jma-xml",
                    SourceConnectionState.Online,
                    DateTimeOffset.UtcNow,
                    Detail: "增量测试")));
        var repository = new SqliteEarthquakeEventRepository(database.Path, source);
        ImmutableArray<EarthquakeReport> cached = LoadReports();
        await repository.InitializeAsync(cached);

        await repository.RefreshAsync();

        Assert.Equal(
            cached.Max(report => report.IssuedAt),
            source.LastSince);
    }

    [Fact]
    public async Task Refresh_FailedSource_KeepsCachedEventsAndUpdatesStatus()
    {
        using var database = new TemporaryDatabase();
        var source = new StubRealtimeSource(new EarthquakeSourceFetchResult(
            [],
            new SourceStatus(
                "p2pquake",
                SourceConnectionState.Disconnected,
                DateTimeOffset.UtcNow,
                Detail: "测试断开")));
        var repository = new SqliteEarthquakeEventRepository(database.Path, source);
        await repository.InitializeAsync(LoadReports());
        ImmutableArray<EarthquakeEvent> before = await repository.ListEventsAsync();

        await repository.RefreshAsync();

        ImmutableArray<EarthquakeEvent> after = await repository.ListEventsAsync();
        Assert.Equal(before.Select(item => item.EventId), after.Select(item => item.EventId));
        Assert.Equal(
            SourceConnectionState.Disconnected,
            Assert.Single(repository.SourceStatuses, status => status.SourceId == "p2pquake").State);
        Assert.Contains("保留已有数据", repository.CacheStatus);
    }

    [Fact]
    public async Task ApplyStreamingResult_PersistsReportAndPreservesChannelStatus()
    {
        using var database = new TemporaryDatabase();
        EarthquakeReport streamingReport = CreateOnlineReport() with
        {
            EventId = "p2pquake:stream-message-1",
            ReportCode = "P2P-551",
            Source = new SourceReference(
                "p2pquake",
                "stream-message-1",
                SourcePayload: "{\"id\":\"stream-message-1\"}"),
        };
        var httpSource = new StubRealtimeSource(new EarthquakeSourceFetchResult(
            [],
            new SourceStatus(
                "p2pquake",
                SourceConnectionState.Online,
                DateTimeOffset.UtcNow,
                Detail: "HTTP 测试源在线")));
        var repository = new SqliteEarthquakeEventRepository(database.Path, httpSource);
        await repository.InitializeAsync(LoadReports());

        await repository.ApplyStreamingResultAsync(new EarthquakeSourceFetchResult(
            [streamingReport],
            new SourceStatus(
                "p2pquake-ws",
                SourceConnectionState.Online,
                streamingReport.ReceivedAt,
                streamingReport.ReceivedAt,
                "WebSocket 测试源在线")));
        DateTimeOffset nextRetryAt = streamingReport.ReceivedAt.AddSeconds(10);
        await repository.ApplyStreamingResultAsync(new EarthquakeSourceFetchResult(
            [],
            new SourceStatus(
                "p2pquake-ws",
                SourceConnectionState.Delayed,
                streamingReport.ReceivedAt.AddSeconds(2),
                streamingReport.ReceivedAt,
                "第 2 次重连等待",
                RetryAttempt: 2,
                NextRetryAt: nextRetryAt,
                ConnectedAt: streamingReport.ReceivedAt.AddSeconds(-3),
                ConnectionEndedAt: streamingReport.ReceivedAt.AddSeconds(2),
                LastError: "远端主动关闭")));
        await repository.RefreshAsync();

        Assert.Contains(
            await repository.ListEventsAsync(),
            item => item.EventId == streamingReport.EventId);
        SourceStatus webSocketStatus = Assert.Single(
            repository.SourceStatuses,
            status => status.SourceId == "p2pquake-ws");
        Assert.Equal(SourceConnectionState.Delayed, webSocketStatus.State);
        Assert.Equal(2, webSocketStatus.RetryAttempt);
        Assert.Equal(nextRetryAt, webSocketStatus.NextRetryAt);
        Assert.Equal(streamingReport.ReceivedAt.AddSeconds(-3), webSocketStatus.ConnectedAt);
        Assert.Equal(streamingReport.ReceivedAt.AddSeconds(2), webSocketStatus.ConnectionEndedAt);
        Assert.Equal("远端主动关闭", webSocketStatus.LastError);
        Assert.Equal(
            SourceConnectionState.Online,
            Assert.Single(
                repository.SourceStatuses,
                status => status.SourceId == "p2pquake").State);

        var reloaded = new SqliteEarthquakeEventRepository(database.Path);
        await reloaded.InitializeAsync([]);
        Assert.Contains(
            await reloaded.ListEventsAsync(),
            item => item.EventId == streamingReport.EventId);
    }

    private static EarthquakeReport CreateOnlineReport()
    {
        DateTimeOffset issuedAt = new(2026, 8, 20, 10, 1, 0, TimeSpan.FromHours(9));
        return new EarthquakeReport
        {
            EventId = "20260820010000",
            ReportCode = "P2P-551",
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            OriginTime = issuedAt.AddMinutes(-1),
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Hypocenter = new Hypocenter(
                "相模湾",
                null,
                new GeoCoordinate(35.1, 139.2),
                10),
            Magnitude = new Magnitude(4.2),
            MaxIntensity = JmaIntensity.Four,
            Source = new SourceReference(
                "p2pquake",
                "20260820010100_0",
                SourcePayload: "{\"eid\":\"20260820010000\"}"),
        };
    }

    private static ImmutableArray<EarthquakeReport> LoadReports()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "TestData");
        string officialRoot = Path.Combine(root, "JmaXml", "Official");
        string syntheticRoot = Path.Combine(root, "JmaXml", "Synthetic");
        var fixtures = new[]
        {
            new JmaXmlFixture(
                Path.Combine(officialRoot, "20260818221220_0_VXSE51_010000.xml"),
                "VXSE51",
                "20260818221220_0_VXSE51_010000.xml"),
            new JmaXmlFixture(
                Path.Combine(officialRoot, "20260818221317_0_VXSE52_270000.xml"),
                "VXSE52",
                "20260818221317_0_VXSE52_270000.xml"),
            new JmaXmlFixture(
                Path.Combine(officialRoot, "20260818221432_0_VXSE53_270000.xml"),
                "VXSE53",
                "20260818221432_0_VXSE53_270000.xml"),
            new JmaXmlFixture(
                Path.Combine(syntheticRoot, "vxse53-correction.xml"),
                "VXSE53",
                "vxse53-correction.xml"),
        };
        string stationPath = Path.Combine(root, "JmaStations.csv");
        return JmaXmlParser.LoadFixtures(
            fixtures,
            JmaStationCatalog.LoadFile(stationPath));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EarthquakeShowTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "cache.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class StubRealtimeSource(
        EarthquakeSourceFetchResult result) : IRealtimeEarthquakeSource
    {
        public string SourceId => result.Status.SourceId;

        public Task<EarthquakeSourceFetchResult> FetchAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class StubIncrementalSource(
        EarthquakeSourceFetchResult result) : IRealtimeEarthquakeSource, IIncrementalEarthquakeSource
    {
        public string SourceId => result.Status.SourceId;

        public DateTimeOffset? LastSince { get; private set; }

        public Task<EarthquakeSourceFetchResult> FetchAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }

        public Task<EarthquakeSourceFetchResult> FetchSinceAsync(
            DateTimeOffset? since,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSince = since;
            return Task.FromResult(result);
        }
    }
}
