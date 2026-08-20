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
    public async Task Initialize_UnsupportedSchema_FallsBackToSeed()
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
        Assert.Contains("回退固定样本", repository.CacheStatus);
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
                "jma-json",
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
            Assert.Single(repository.SourceStatuses, status => status.SourceId == "jma-json").State);
        Assert.Contains("JMA JSON 已更新 1 条报文", repository.CacheStatus);

        var reloaded = new SqliteEarthquakeEventRepository(database.Path);
        await reloaded.InitializeAsync([]);
        Assert.Equal(2, (await reloaded.ListEventsAsync()).Length);
    }

    [Fact]
    public async Task Refresh_FailedSource_KeepsCachedEventsAndUpdatesStatus()
    {
        using var database = new TemporaryDatabase();
        var source = new StubRealtimeSource(new EarthquakeSourceFetchResult(
            [],
            new SourceStatus(
                "jma-json",
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
            Assert.Single(repository.SourceStatuses, status => status.SourceId == "jma-json").State);
        Assert.Contains("保留已有数据", repository.CacheStatus);
    }

    private static EarthquakeReport CreateOnlineReport()
    {
        DateTimeOffset issuedAt = new(2026, 8, 20, 10, 1, 0, TimeSpan.FromHours(9));
        return new EarthquakeReport
        {
            EventId = "20260820010000",
            ReportCode = "JMA-JSON",
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
                "jma-json",
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
}
