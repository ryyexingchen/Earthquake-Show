using EarthquakeShow.App.ViewModels;
using EarthquakeShow.App.Services;
using EarthquakeShow.App.Views;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
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
            Assert.NotNull(viewModel.Map.BoundaryGeometry);
            Assert.Equal(1069, viewModel.Map.BoundaryGeometry.Boundaries.Length);
            Assert.Equal(8, viewModel.Map.BoundaryGeometry.GetForArea("100").Count);
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
                var detailsView = new EarthquakeDetailsView
                {
                    DataContext = new IntensityBadgeTestContext(
                        new EarthquakeSummaryOverviewViewModel(
                            new EarthquakeIntensityDisplayViewModel("6强", "SixUpper"),
                            string.Empty,
                            false,
                            string.Empty,
                            false,
                            string.Empty,
                            false,
                            false,
                            new EarthquakeTsunamiStatusViewModel("津波 调查中", "Investigating"))),
                };
                detailsView.Measure(new Size(380, 720));
                detailsView.Arrange(new Rect(0, 0, 380, 720));
                detailsView.UpdateLayout();

                Border badge = Assert.IsType<Border>(
                    detailsView.FindName("SummaryMaximumIntensityBadge"));
                Assert.Equal(
                    Color.FromRgb(142, 36, 170),
                    Assert.IsType<SolidColorBrush>(badge.Background).Color);
                Assert.Equal(
                    Colors.White,
                    Assert.IsType<SolidColorBrush>(badge.BorderBrush).Color);
                TextBlock badgeText = Assert.IsType<TextBlock>(badge.Child);
                Assert.Equal(
                    Colors.White,
                    Assert.IsType<SolidColorBrush>(badgeText.Foreground).Color);
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

    private sealed record IntensityBadgeTestContext(
        EarthquakeSummaryOverviewViewModel SummaryOverview);

    [Fact]
    public async Task Initialize_StreamingSource_PublishesReportAndStopsOnDispose()
    {
        string cachePath = CreateTemporaryCachePath();
        EarthquakeReport report = CreateStreamingReport();
        var source = new StubStreamingSource(new EarthquakeSourceFetchResult(
            [report],
            new SourceStatus(
                "p2pquake-ws",
                SourceConnectionState.Online,
                report.ReceivedAt,
                report.ReceivedAt,
                "测试 WebSocket 在线")));
        var viewModel = new MainWindowViewModel(
            cachePath,
            enableNetwork: false,
            streamingSource: source);

        try
        {
            await viewModel.InitializeAsync();
            await WaitUntilAsync(() => viewModel.EarthquakePage.State.Events.Any(
                item => item.EventId == report.EventId));

            Assert.Contains(
                viewModel.EarthquakePage.State.SourceStatuses,
                status => status.SourceId == "p2pquake-ws" &&
                    status.State == SourceConnectionState.Online);
            Assert.Contains("p2pquake-ws 已更新 1 条报文", viewModel.CacheStatus);
            Assert.Equal(
                "WebSocket：已连接 · 活性：等待首条消息",
                viewModel.EarthquakePage.Display.WebSocketStatusText);

            viewModel.Dispose();
            await source.Stopped.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            viewModel.Dispose();
            DeleteTemporaryCache(cachePath);
        }
    }

    [Fact]
    public async Task ApplySettings_PersistsAndRecreatesInjectedStreamingSource()
    {
        string cachePath = CreateTemporaryCachePath();
        string settingsPath = Path.Combine(
            Path.GetDirectoryName(cachePath)!,
            "settings.json");
        int factoryCalls = 0;
        EarthquakeReport report = CreateStreamingReport();
        var viewModel = new MainWindowViewModel(
            cachePath,
            enableNetwork: false,
            settingsPath: settingsPath,
            streamingSourceFactory: _ =>
            {
                factoryCalls++;
                return new StubStreamingSource(new EarthquakeSourceFetchResult(
                    [report],
                    new SourceStatus(
                        "p2pquake-ws",
                        SourceConnectionState.Online,
                        report.ReceivedAt,
                        report.ReceivedAt,
                        "测试 WebSocket 在线")));
            });

        try
        {
            await viewModel.InitializeAsync();
            Assert.Equal(1, factoryCalls);

            viewModel.Settings.KeepAliveSeconds = 60;
            viewModel.Settings.MaxConnectionDurationMinutes = 8;
            await viewModel.Settings.ApplyAsync();

            Assert.Equal(2, factoryCalls);
            ApplicationSettingsLoadResult loaded =
                new ApplicationSettingsStore(settingsPath).Load();
            Assert.Equal(
                new WebSocketConnectionSettings(60, 8),
                loaded.Settings.WebSocketSettings);
        }
        finally
        {
            viewModel.Dispose();
            DeleteTemporaryCache(cachePath);
        }
    }

    private static EarthquakeReport CreateStreamingReport()
    {
        DateTimeOffset issuedAt = new(2026, 8, 20, 12, 8, 7, TimeSpan.FromHours(9));
        return new EarthquakeReport
        {
            EventId = "p2pquake:app-stream-message-1",
            ReportCode = "P2P-551",
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            OriginTime = issuedAt.AddMinutes(-4),
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Hypocenter = new Hypocenter(
                "熊本県熊本地方",
                null,
                new GeoCoordinate(32.4, 130.6),
                10),
            Magnitude = new Magnitude(2.9),
            MaxIntensity = JmaIntensity.Three,
            Source = new SourceReference(
                "p2pquake",
                "app-stream-message-1",
                SourcePayload: "{\"id\":\"app-stream-message-1\"}"),
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
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

    private sealed class StubStreamingSource(EarthquakeSourceFetchResult result)
        : IStreamingEarthquakeSource
    {
        private readonly TaskCompletionSource _stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string SourceId => result.Status.SourceId;

        public Task Stopped => _stopped.Task;

        public async IAsyncEnumerable<EarthquakeSourceFetchResult> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return result;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                _stopped.TrySetResult();
            }
        }
    }
}
