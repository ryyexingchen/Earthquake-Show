using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EarthquakeShow.App.Services;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using EarthquakeShow.Infrastructure.Sources;
using EarthquakeShow.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace EarthquakeShow.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly DispatcherTimer _clockTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _streamingRestartGate = new(1, 1);
    private readonly ApplicationSettingsStore _settingsStore;
    private readonly SqliteEarthquakeEventRepository _repository;
    private readonly SqliteTsunamiReportRepository _tsunamiRepository;
    private readonly IReadOnlyList<EarthquakeReport> _seedReports;
    private readonly HttpClient? _httpClient;
    private readonly JmaJsonEarthquakeSource? _realtimeSource;
    private readonly IRealtimeTsunamiSource? _tsunamiSource;
    private readonly IReadOnlyList<IRealtimeEarthquakeSource> _realtimeSources = [];
    private readonly Func<WebSocketConnectionSettings, IStreamingEarthquakeSource>? _streamingSourceFactory;
    private IStreamingEarthquakeSource? _streamingSource;
    private readonly RefreshBackoffPolicy _refreshBackoffPolicy = new();
    private Task? _refreshLoopTask;
    private Task? _streamingLoopTask;
    private Task? _initializationTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _streamingSessionCancellation;
    private string _currentTime = string.Empty;
    private string _cacheStatus = "缓存：初始化中";
    private string _autoRefreshStatus = "自动刷新：未启动";
    private ImmutableArray<SourceStatus> _tsunamiSourceStatuses = [];
    private ApplicationSettings _applicationSettings;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _resourcesDisposed;

    public MainWindowViewModel(
        string? cachePath = null,
        bool enableNetwork = true,
        IStreamingEarthquakeSource? streamingSource = null,
        string? settingsPath = null,
        Func<WebSocketConnectionSettings, IStreamingEarthquakeSource>? streamingSourceFactory = null,
        IRealtimeTsunamiSource? tsunamiSource = null)
    {
        AppVersion = GetAppVersion();
        _settingsStore = new(settingsPath ?? GetDefaultSettingsPath());
        ApplicationSettingsLoadResult settingsLoad = _settingsStore.Load();
        _applicationSettings = settingsLoad.Settings;
        Settings = new(settingsLoad, ApplyWebSocketSettingsAsync);
        JmaStationCoordinateCatalog stationCatalog = FixedJmaXmlDataLoader.LoadStationCatalog();
        _seedReports = FixedJmaXmlDataLoader.LoadReports(stationCatalog);
        if (enableNetwork)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EarthquakeShow/0.41.0");
            _realtimeSource = new JmaJsonEarthquakeSource(_httpClient);
            JmaXmlEarthquakeSource xmlSource = new(
                _httpClient,
                stationCatalog: stationCatalog);
            P2pQuakeEarthquakeSource p2pQuakeSource = new(_httpClient);
            _realtimeSources = [_realtimeSource, xmlSource, p2pQuakeSource];
            _tsunamiSource = tsunamiSource ?? new JmaTsunamiXmlSource(_httpClient);
            _streamingSourceFactory = streamingSource is null
                ? streamingSourceFactory ?? CreateStreamingSource
                : streamingSourceFactory;
            _streamingSource = streamingSource ??
                (_streamingSourceFactory ?? CreateStreamingSource)(
                    _applicationSettings.WebSocketSettings);
        }
        else
        {
            _tsunamiSource = tsunamiSource;
            _streamingSourceFactory = streamingSourceFactory;
            _streamingSource = streamingSource ??
                streamingSourceFactory?.Invoke(_applicationSettings.WebSocketSettings);
        }

        _repository = new SqliteEarthquakeEventRepository(
            cachePath ?? GetDefaultCachePath(),
            _realtimeSources,
            stationCatalog);
        _tsunamiRepository = new SqliteTsunamiReportRepository(
            cachePath ?? GetDefaultCachePath(),
            _tsunamiSource);
        EarthquakePage = new EarthquakePageViewModel(
            _repository);
        EventList = new EarthquakeEventListViewModel(EarthquakePage);
        string mapRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Data",
            "Map");
        Map = new EarthquakeMapViewModel(
            EarthquakePage,
            OfflineMapGeometry.LoadFromFile(
                Path.Combine(
                    mapRoot,
                    "jma-earthquake-areas-overview.geojson")),
            OfflineMapGeometry.LoadFromFile(
                Path.Combine(
                    mapRoot,
                    "jma-earthquake-municipalities-overview.geojson")),
            OfflineMapBoundaryGeometry.LoadFromFile(
                Path.Combine(
                    mapRoot,
                    "jma-earthquake-area-boundaries-overview.geojson")),
            new MapLodResourceProvider(
                Path.Combine(mapRoot, "jma-earthquake-areas-medium.geojson"),
                Path.Combine(mapRoot, "jma-earthquake-municipalities-medium.geojson"),
                Path.Combine(mapRoot, "jma-earthquake-area-boundaries-medium.geojson")));
        Details = new EarthquakeDetailsViewModel(EarthquakePage, Map);
        Layout = new WindowLayoutViewModel();
        UpdateClock();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentTime
    {
        get => _currentTime;
        private set
        {
            if (_currentTime == value)
            {
                return;
            }

            _currentTime = value;
            OnPropertyChanged();
        }
    }

    public string CacheStatus
    {
        get => _cacheStatus;
        private set
        {
            if (_cacheStatus == value)
            {
                return;
            }

            _cacheStatus = value;
            OnPropertyChanged();
        }
    }

    public string AutoRefreshStatus
    {
        get => _autoRefreshStatus;
        private set
        {
            if (_autoRefreshStatus == value)
            {
                return;
            }

            _autoRefreshStatus = value;
            OnPropertyChanged();
        }
    }

    public string MapDataStatus => Map.IsOfficialBoundary ? "地图：离线边界" : "地图：离线示意";

    public string AppVersion { get; }

    public EarthquakePageViewModel EarthquakePage { get; }

    public EarthquakeEventListViewModel EventList { get; }

    public EarthquakeMapViewModel Map { get; }

    public EarthquakeDetailsViewModel Details { get; }

    public WindowLayoutViewModel Layout { get; }

    public SettingsViewModel Settings { get; }

    public ImmutableArray<SourceStatus> TsunamiSourceStatuses => _tsunamiSourceStatuses;

    public ValueTask InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _initializationTask ??= InitializeCoreAsync(cancellationToken);
        return new ValueTask(_initializationTask);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource? linkedCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token)
            : null;
        CancellationToken token = linkedCancellation?.Token ?? _lifetimeCancellation.Token;
        await _repository.InitializeAsync(_seedReports, token);
        CacheStatus = _repository.CacheStatus;
        await _tsunamiRepository.InitializeAsync(token);
        UpdateTsunamiSourceStatuses();
        EarthquakePage.SetSourceState(
            _repository.SourceStatuses,
            isOffline: true);
        await EarthquakePage.LoadAsync(token);
        if (_realtimeSource is not null || _tsunamiSource is not null)
        {
            await RefreshFromNetworkAsync(token);
            _refreshLoopTask ??= RunRefreshLoopAsync(_lifetimeCancellation.Token);
        }

        if (_streamingSource is not null)
        {
            StartStreamingLoop();
        }

        _isInitialized = true;
    }

    public void Dispose()
    {
        _ = GetOrStartDisposeTask();
    }

    public ValueTask DisposeAsync() => new(GetOrStartDisposeTask());

    private Task GetOrStartDisposeTask()
    {
        _disposeTask ??= DisposeAsyncCore();
        return _disposeTask;
    }

    private async Task DisposeAsyncCore()
    {
        if (!BeginDispose())
        {
            return;
        }

        try
        {
            await AwaitShutdownTaskAsync(_initializationTask).ConfigureAwait(true);
            await AwaitShutdownTaskAsync(_refreshLoopTask).ConfigureAwait(true);
            await AwaitShutdownTaskAsync(_streamingLoopTask).ConfigureAwait(true);
            await _streamingRestartGate.WaitAsync().ConfigureAwait(true);
            _streamingRestartGate.Release();
        }
        finally
        {
            DisposeResources();
        }
    }

    private bool BeginDispose()
    {
        if (_isDisposed)
        {
            return false;
        }

        _isDisposed = true;
        _streamingSessionCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        return true;
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        Details.Dispose();
        Map.Dispose();
        EventList.Dispose();
        EarthquakePage.Dispose();
        _tsunamiRepository.Dispose();
        _httpClient?.Dispose();
        _streamingSessionCancellation?.Dispose();
        _streamingSessionCancellation = null;
        _streamingRestartGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private static async Task AwaitShutdownTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void OpenSettings() => Settings.IsVisible = true;

    public void CloseSettings() => Settings.IsVisible = false;

    private async Task RefreshFromNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EarthquakePage.RefreshAsync(cancellationToken);
            if (_tsunamiSource is not null)
            {
                try
                {
                    await _tsunamiRepository.RefreshAsync(cancellationToken);
                    UpdateTsunamiSourceStatuses();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or SqliteException or
                    InvalidDataException or FormatException or ArgumentException or
                    InvalidOperationException)
                {
                    UpdateTsunamiSourceStatuses();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void UpdateTsunamiSourceStatuses()
    {
        ImmutableArray<SourceStatus> statuses = _tsunamiRepository.SourceStatuses;
        if (_tsunamiSourceStatuses == statuses)
        {
            return;
        }

        _tsunamiSourceStatuses = statuses;
        OnPropertyChanged(nameof(TsunamiSourceStatuses));
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = _refreshBackoffPolicy.GetNextDelay(
                    EarthquakePage.State.SourceStatuses);
                AutoRefreshStatus = $"自动刷新：{FormatDelay(delay)} 后检查";
                await Task.Delay(delay, cancellationToken);
                AutoRefreshStatus = "自动刷新：检查中";
                await RefreshFromNetworkAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AutoRefreshStatus = "自动刷新：已停止";
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            AutoRefreshStatus = "自动刷新：已停止";
        }
    }

    private async Task RunStreamingLoopAsync(
        IStreamingEarthquakeSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EarthquakeSourceFetchResult result in
                source.StreamAsync(cancellationToken))
            {
                await _repository.ApplyStreamingResultAsync(result, cancellationToken);
                if (!_isDisposed)
                {
                    CacheStatus = _repository.CacheStatus;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isDisposed)
            {
                CacheStatus = $"缓存：WebSocket 流已停止（{exception.Message}）";
            }
        }
    }

    private void StartStreamingLoop()
    {
        if (_streamingSource is null || _streamingLoopTask is not null || _isDisposed)
        {
            return;
        }

        _streamingSessionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _streamingLoopTask = RunStreamingLoopAsync(
            _streamingSource,
            _streamingSessionCancellation.Token);
    }

    private async Task ApplyWebSocketSettingsAsync(WebSocketConnectionSettings settings)
    {
        settings.Validate();
        ApplicationSettings applicationSettings = new(
            ApplicationSettings.CurrentSchemaVersion,
            settings);
        await _settingsStore.SaveAsync(applicationSettings, _lifetimeCancellation.Token)
            .ConfigureAwait(true);
        _applicationSettings = applicationSettings;

        if (_streamingSourceFactory is null || _isDisposed)
        {
            return;
        }

        await _streamingRestartGate.WaitAsync(_lifetimeCancellation.Token)
            .ConfigureAwait(true);
        try
        {
            CancellationTokenSource? previousCancellation = _streamingSessionCancellation;
            Task? previousLoop = _streamingLoopTask;
            previousCancellation?.Cancel();
            if (previousLoop is not null)
            {
                await previousLoop.ConfigureAwait(true);
            }

            previousCancellation?.Dispose();
            _streamingSessionCancellation = null;
            _streamingLoopTask = null;
            _streamingSource = _streamingSourceFactory(settings);
            if (_isInitialized)
            {
                StartStreamingLoop();
            }
        }
        finally
        {
            _streamingRestartGate.Release();
        }
    }

    private static IStreamingEarthquakeSource CreateStreamingSource(
        WebSocketConnectionSettings settings) =>
        new ReconnectingEarthquakeSource(
            new P2pQuakeWebSocketSource(
                keepAliveInterval: settings.KeepAliveInterval),
            new StreamingReconnectPolicy(
                maxConnectionDuration: settings.MaxConnectionDuration));

    private static string FormatDelay(TimeSpan delay)
    {
        return delay.TotalMinutes >= 1
            ? $"{delay.TotalMinutes:0.#} 分钟"
            : $"{Math.Max(1, delay.TotalSeconds):0} 秒";
    }

    private static string GetDefaultCachePath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "EarthquakeShow", "earthquake-cache.db");
    }

    private static string GetDefaultSettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "EarthquakeShow", "settings.json");
    }

    private static string GetAppVersion()
    {
        Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        UpdateClock();
    }

    private void UpdateClock()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset japanTime = TimeZoneInfo.ConvertTime(now, JapanTimeZone);
        CurrentTime = japanTime.ToString("yyyy-MM-dd HH:mm:ss 'JST'", CultureInfo.InvariantCulture);
        EarthquakePage.UpdateDisplayClock(now);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
