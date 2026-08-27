using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Persistence;
using EarthquakeShow.Infrastructure.Sources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed partial class SqliteEarthquakeEventRepositoryTests
{
    [Fact]
    public async Task ImportLocalXmlAsync_SavesReportsAndPublishesMergedEvent()
    {
        using var database = new TemporaryDatabase();
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string sourcePath = OfficialFixture("20260818221432_0_VXSE53_270000.xml");
        string localPath = Path.Combine(directory.Path, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, localPath);
        var repository = new SqliteEarthquakeEventRepository(database.Path);
        int changeNotifications = 0;
        repository.EventsChanged += (_, _) => changeNotifications++;

        await repository.InitializeAsync([]);
        JmaXmlLocalFileImportResult result = await repository.ImportLocalXmlAsync(
            new JmaXmlLocalFileImporter(),
            directory.Path);

        Assert.Equal(1, result.SavedReportCount);
        Assert.Equal(2, changeNotifications);
        EarthquakeEvent earthquakeEvent = Assert.Single(await repository.ListEventsAsync());
        EarthquakeReport report = Assert.Single(earthquakeEvent.Reports);
        Assert.Equal(Path.GetFileName(localPath), report.Source.SourceMessageId);
    }

    [Fact]
    public async Task ImportLocalXmlAsync_IsIdempotentForSameFile()
    {
        using var database = new TemporaryDatabase();
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string sourcePath = OfficialFixture("20260818221432_0_VXSE53_270000.xml");
        File.Copy(sourcePath, Path.Combine(directory.Path, Path.GetFileName(sourcePath)));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "ignored.xml"),
            "not a JMA earthquake report");
        var repository = new SqliteEarthquakeEventRepository(database.Path);
        await repository.InitializeAsync([]);

        await repository.ImportLocalXmlAsync(new JmaXmlLocalFileImporter(), directory.Path);
        await repository.ImportLocalXmlAsync(new JmaXmlLocalFileImporter(), directory.Path);

        EarthquakeEvent earthquakeEvent = Assert.Single(await repository.ListEventsAsync());
        Assert.Single(earthquakeEvent.Reports);
    }

    [Fact]
    public async Task ImportLocalXmlAsync_SavesValidReportsWhenAnotherFileFails()
    {
        using var database = new TemporaryDatabase();
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string sourcePath = OfficialFixture("20260818221432_0_VXSE53_270000.xml");
        File.Copy(sourcePath, Path.Combine(directory.Path, Path.GetFileName(sourcePath)));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "20260818221433_0_VXSE53_270000.xml"),
            "<Report>");
        var repository = new SqliteEarthquakeEventRepository(database.Path);
        await repository.InitializeAsync([]);

        JmaXmlLocalFileImportResult result = await repository.ImportLocalXmlAsync(
            new JmaXmlLocalFileImporter(),
            directory.Path);

        Assert.Equal(1, result.SavedReportCount);
        Assert.Single(result.Failures);
        Assert.Single((await repository.ListEventsAsync()).SelectMany(item => item.Reports));
    }

    [Fact]
    public async Task ImportLocalXmlAsync_ReloadsLatestBatchDetailsFromDatabase()
    {
        using var database = new TemporaryDatabase();
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string sourcePath = OfficialFixture("20260818221432_0_VXSE53_270000.xml");
        File.Copy(sourcePath, Path.Combine(directory.Path, Path.GetFileName(sourcePath)));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "ignored.xml"),
            "not a JMA earthquake report");

        var writer = new SqliteEarthquakeEventRepository(database.Path);
        await writer.InitializeAsync([]);
        await writer.ImportLocalXmlAsync(new JmaXmlLocalFileImporter(), directory.Path);

        var reader = new SqliteEarthquakeEventRepository(database.Path);
        await reader.InitializeAsync([]);
        JmaXmlLocalFileImportHistory? history = await reader.GetLatestLocalXmlImportAsync();
        Assert.NotNull(history);

        Assert.Equal(Path.GetFullPath(directory.Path), history.DirectoryPath);
        Assert.Equal(1, history.SavedReportCount);
        Assert.Contains(history.Items, item => item.IsSkipped && item.FilePath.EndsWith("ignored.xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportLocalXmlAsync_RollsBackReportsWhenHistoryWriteFails()
    {
        using var database = new TemporaryDatabase();
        using TemporaryDirectory directory = TemporaryDirectory.Create();
        string sourcePath = OfficialFixture("20260818221432_0_VXSE53_270000.xml");
        File.Copy(sourcePath, Path.Combine(directory.Path, Path.GetFileName(sourcePath)));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "ignored.xml"),
            "not a JMA earthquake report");
        var repository = new SqliteEarthquakeEventRepository(database.Path);
        await repository.InitializeAsync([]);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Pooling = false,
        }.ConnectionString;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_local_xml_detail AFTER INSERT ON local_xml_import_items BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => repository.ImportLocalXmlAsync(
            new JmaXmlLocalFileImporter(),
            directory.Path));

        Assert.Empty((await repository.ListEventsAsync()).SelectMany(item => item.Reports));
        Assert.Null(await repository.GetLatestLocalXmlImportAsync());
    }

    private static string OfficialFixture(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "tests", "TestData", "JmaXml", "Official", fileName);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"earthquake-show-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
