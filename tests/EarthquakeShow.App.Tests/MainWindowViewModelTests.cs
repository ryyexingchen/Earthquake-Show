using EarthquakeShow.App.ViewModels;
using System.Threading;
using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task Initialize_FixedJmaXml_LoadsMergedCorrectionEvent()
    {
        string cachePath = CreateTemporaryCachePath();
        using var viewModel = new MainWindowViewModel(cachePath, enableNetwork: false);

        try
        {
            await viewModel.InitializeAsync();

            EarthquakeEvent earthquakeEvent = Assert.Single(viewModel.EarthquakePage.State.Events);
            Assert.Equal(4, earthquakeEvent.Reports.Length);
            Assert.Equal(ReportStatus.Correction, earthquakeEvent.Summary?.Status);
            Assert.Equal(3.9, earthquakeEvent.Summary?.Magnitude?.Value);
            Assert.Equal(75, earthquakeEvent.LatestReport?.IntensityStations.Length);
            Assert.All(
                earthquakeEvent.LatestReport!.IntensityStations,
                station => Assert.NotNull(station.Coordinate));
            Assert.Single(viewModel.EventList.Items);
            Assert.Equal(76, viewModel.Map.Markers.Count);
        }
        finally
        {
            DeleteTemporaryCache(cachePath);
        }
    }

    [Fact]
    public void MainWindow_XamlResources_LoadOnStaThread()
    {
        Exception? capturedException = null;
        var thread = new Thread(() =>
        {
            string cachePath = CreateTemporaryCachePath();
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow(cachePath, enableNetwork: false);
                window.Close();
                app.Shutdown();
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF 窗口初始化超时。");
        Assert.Null(capturedException);
    }

    private static string CreateTemporaryCachePath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "EarthquakeShowAppTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "cache.db");
    }

    private static void DeleteTemporaryCache(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
