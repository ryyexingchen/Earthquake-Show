using System.Collections.Immutable;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Persistence;
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
}
