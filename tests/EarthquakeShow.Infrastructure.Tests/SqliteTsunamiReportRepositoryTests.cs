using System.Collections.Immutable;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Persistence;
using EarthquakeShow.Infrastructure.Sources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class SqliteTsunamiReportRepositoryTests
{
    [Fact]
    public async Task SaveStationCatalogAsync_PersistsStationsAndPublicationMappings()
    {
        using var database = new TemporaryDatabase();
        var repository = new SqliteTsunamiReportRepository(database.Path);
        JmaTsunamiStationCatalog catalog = JmaTsunamiStationCatalog.LoadJson("""
            {
              "sourceVersion": "test",
              "stations": [{ "stationCode": "10050", "name": "釧路沖１００ｋｍＡ", "latitude": 42.0, "longitude": 144.0 }],
              "offshorePublicationMappings": [{ "publicationCode": "00410", "name": "釧路沖１００ｋｍ", "stationCodes": ["10050"] }]
            }
            """);

        await repository.SaveStationCatalogAsync(catalog);

        JmaTsunamiStationCatalog loaded = await repository.LoadStationCatalogAsync();
        Assert.True(loaded.TryGetStation("10050", out JmaTsunamiStationCatalogEntry station));
        Assert.Equal(42.0, station.Latitude);
        Assert.True(loaded.TryGetPublication("00410", out JmaTsunamiPublicationCatalogEntry publication));
        Assert.Equal("10050", Assert.Single(publication.StationCodes));

        await using var connection = new SqliteConnection($"Data Source={database.Path}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.publication_code, m.station_code, s.name
            FROM tsunami_offshore_publication p
            JOIN tsunami_offshore_station_map m ON m.publication_code = p.publication_code
            JOIN tsunami_station_catalog s ON s.station_code = m.station_code;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("00410", reader.GetString(0));
        Assert.Equal("10050", reader.GetString(1));
        Assert.Equal("釧路沖１００ｋｍＡ", reader.GetString(2));
    }
    [Fact]
    public async Task SaveAndReload_OfficialReports_PreservesStructuredFieldsAndRawXml()
    {
        using var database = new TemporaryDatabase();
        ImmutableArray<JmaTsunamiReport> reports = LoadOfficialReports();
        var repository = new SqliteTsunamiReportRepository(database.Path);

        await repository.InitializeAsync();
        await repository.SaveReportsAsync(reports);

        ImmutableArray<JmaTsunamiReport> loaded = await repository.ListReportsAsync();

        Assert.Equal(3, loaded.Length);
        JmaTsunamiReport vtse41 = Assert.Single(loaded, report => report.ReportCode == "VTSE41");
        Assert.Equal("20160901071000", vtse41.EventId);
        Assert.Equal(ReportContext.Training, vtse41.Context);
        JmaTsunamiForecastArea vtse41Area = Assert.Single(
            vtse41.ForecastAreas,
            area => area.Code == "300");
        Assert.Equal("大津波警報：発表", vtse41Area.KindName);
        Assert.NotNull(vtse41Area.MaximumHeight);
        Assert.Null(vtse41Area.MaximumHeight!.Meters);
        Assert.Equal("巨大", vtse41Area.MaximumHeight.Description);
        Assert.Contains(
            vtse41.FixedAdditionalTexts,
            text => text.Contains("東日本大震災クラスの津波が来襲します。", StringComparison.Ordinal));
        Assert.Contains("<Report", vtse41.Source.SourcePayload);

        JmaTsunamiReport vtse51 = Assert.Single(loaded, report => report.ReportCode == "VTSE51");
        Assert.Equal(182, vtse51.ForecastAreas.SelectMany(area => area.Stations).Count());
        Assert.Equal("大洗", Assert.Single(
            vtse51.ForecastAreas.SelectMany(area => area.Stations),
            station => station.Code == "30001").Name);

        JmaTsunamiReport vtse52 = Assert.Single(loaded, report => report.ReportCode == "VTSE52");
        Assert.Equal(8, vtse52.ObservationStations.Length);
        Assert.Equal(5, vtse52.EstimationAreas.Length);
        Assert.Equal(1.8, Assert.Single(
            vtse52.ObservationStations,
            station => station.Code == "38090").MaximumHeight!.Meters);
    }

    [Fact]
    public async Task SaveReports_UsesEventFilterAndUpsertsBySourceMessageIdentity()
    {
        using var database = new TemporaryDatabase();
        ImmutableArray<JmaTsunamiReport> reports = LoadOfficialReports();
        var repository = new SqliteTsunamiReportRepository(database.Path);

        await repository.SaveReportsAsync(reports);
        JmaTsunamiReport replacement = reports[0] with
        {
            HeadlineText = "更新后的测试海啸标题",
        };
        await repository.SaveReportsAsync([replacement]);

        ImmutableArray<JmaTsunamiReport> eventReports = await repository
            .ListReportsForEventAsync("20160901071000");
        Assert.Equal(3, eventReports.Length);
        Assert.Equal("更新后的测试海啸标题", Assert.Single(
            eventReports,
            report => report.Source.SourceMessageId == replacement.Source.SourceMessageId).HeadlineText);
        Assert.Empty(await repository.ListReportsForEventAsync("missing-event"));
    }

    [Fact]
    public async Task Initialize_LegacySchemaV1_MigratesToSchemaV2()
    {
        using var database = new TemporaryDatabase();
        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO schema_info VALUES ('schema_version', '1');";
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteTsunamiReportRepository(database.Path);
        await repository.InitializeAsync();

        await using (var verify = new SqliteConnection($"Data Source={database.Path}"))
        {
            await verify.OpenAsync();
            await using SqliteCommand verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
            Assert.Equal("2", (string?)await verifyCommand.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Refresh_UsesLatestIssuedAt_PersistsStatusAndKeepsReportsOnFailure()
    {
        using var database = new TemporaryDatabase();
        JmaTsunamiReport report = LoadOfficialReports()[0] with
        {
            Source = LoadOfficialReports()[0].Source with { SourceId = "test-tsunami" },
        };
        var source = new StubTsunamiSource(
            new TsunamiSourceFetchResult(
                [report],
                new SourceStatus(
                    "test-tsunami",
                    SourceConnectionState.Online,
                    report.ReceivedAt,
                    report.ReceivedAt,
                    "测试海啸源在线")));
        var repository = new SqliteTsunamiReportRepository(database.Path, source);

        await repository.InitializeAsync();
        Assert.Equal(SourceConnectionState.Disabled, Assert.Single(repository.SourceStatuses).State);

        await repository.RefreshAsync();
        Assert.Null(source.LastSince);
        Assert.Equal(SourceConnectionState.Online, Assert.Single(repository.SourceStatuses).State);
        Assert.Single(await repository.ListReportsAsync());

        source.Result = new TsunamiSourceFetchResult(
            [],
            new SourceStatus(
                "test-tsunami",
                SourceConnectionState.Disconnected,
                report.ReceivedAt.AddMinutes(1),
                Detail: "测试网络失败"));
        await repository.RefreshAsync();

        Assert.Equal(report.IssuedAt, source.LastSince);
        Assert.Single(await repository.ListReportsAsync());
        Assert.Equal(
            SourceConnectionState.Disconnected,
            Assert.Single(repository.SourceStatuses).State);

        repository.Dispose();
        var reloaded = new SqliteTsunamiReportRepository(database.Path, source);
        await reloaded.InitializeAsync();
        Assert.Equal(SourceConnectionState.Disabled, Assert.Single(reloaded.SourceStatuses).State);
        Assert.Equal(report.ReceivedAt, Assert.Single(reloaded.SourceStatuses).LastReceivedAt);
        reloaded.Dispose();
    }

    private static ImmutableArray<JmaTsunamiReport> LoadOfficialReports()
    {
        string directory = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "TestData", "JmaTsunami", "Official");
        string[] files =
        [
            "32-39_12_02_250206_VTSE41.xml",
            "32-39_12_03_250206_VTSE51.xml",
            "32-39_12_05_250206_VTSE52.xml",
        ];
        return files.Select(file =>
        {
            string path = Path.Combine(directory, file);
            string code = file.Contains("VTSE41", StringComparison.Ordinal)
                ? "VTSE41"
                : file.Contains("VTSE51", StringComparison.Ordinal)
                    ? "VTSE51"
                    : "VTSE52";
            string xml = File.ReadAllText(path);
            return JmaTsunamiXmlParser.Parse(
                xml,
                new JmaTsunamiXmlParseOptions(
                    code,
                    new SourceReference("jma-xml-tsunami", file, new Uri(path), xml),
                    ReceivedAt: new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(8))));
        }).ToImmutableArray();
    }

    private sealed class StubTsunamiSource(TsunamiSourceFetchResult result) :
        IRealtimeTsunamiSource,
        IIncrementalTsunamiSource
    {
        public TsunamiSourceFetchResult Result { get; set; } = result;

        public DateTimeOffset? LastSince { get; private set; }

        public string SourceId => Result.Status.SourceId;

        public Task<TsunamiSourceFetchResult> FetchAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }

        public Task<TsunamiSourceFetchResult> FetchSinceAsync(
            DateTimeOffset? since,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSince = since;
            return Task.FromResult(Result);
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"earthquake-show-tsunami-{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
                File.Delete($"{Path}-shm");
                File.Delete($"{Path}-wal");
            }
            catch (IOException)
            {
                // 测试清理失败不影响断言结果。
            }
        }
    }
}
