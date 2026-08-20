using EarthquakeShow.App.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class ApplicationSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        string path = CreateTemporaryPath();
        try
        {
            ApplicationSettingsLoadResult result =
                new ApplicationSettingsStore(path).Load();

            Assert.Equal(30, result.Settings.WebSocketSettings.KeepAliveSeconds);
            Assert.Equal(9, result.Settings.WebSocketSettings.MaxConnectionDurationMinutes);
            Assert.Null(result.Warning);
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsWebSocketSettings()
    {
        string path = CreateTemporaryPath();
        try
        {
            var settings = new ApplicationSettings(
                ApplicationSettings.CurrentSchemaVersion,
                new WebSocketConnectionSettings(60, 8));
            var store = new ApplicationSettingsStore(path);

            await store.SaveAsync(settings);
            ApplicationSettingsLoadResult result = store.Load();

            Assert.Equal(settings, result.Settings);
            Assert.Null(result.Warning);
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public async Task Save_InvalidSettings_ThrowsBeforeWriting()
    {
        string path = CreateTemporaryPath();
        try
        {
            var store = new ApplicationSettingsStore(path);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.SaveAsync(new ApplicationSettings(
                    WebSocket: new WebSocketConnectionSettings(5, 9))));
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public void Load_InvalidJson_ReturnsDefaultsAndWarning()
    {
        string path = CreateTemporaryPath();
        try
        {
            File.WriteAllText(path, "{ invalid");

            ApplicationSettingsLoadResult result =
                new ApplicationSettingsStore(path).Load();

            Assert.Equal(ApplicationSettings.Default, result.Settings);
            Assert.Contains("回退默认值", result.Warning);
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public void Validate_RejectsUnsupportedRotationBeyondServerLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WebSocketConnectionSettings(30, 10).Validate());
    }

    private static string CreateTemporaryPath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "EarthquakeShowSettingsTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }

    private static void DeleteTemporaryPath(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
