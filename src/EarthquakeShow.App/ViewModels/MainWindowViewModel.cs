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
    private readonly IReadOnlyList<JmaTsunamiReport> _seedTsunamiReports;
    private readonly JmaStationCoordinateCatalog _stationCatalog;
    private readonly JmaTsunamiStationCatalog _tsunamiStationCatalog;
    private readonly JmaIntensityRegionCatalog? _regionCatalog;
    private readonly HttpClient? _httpClient;
    private readonly IRealtimeTsunamiSource? _tsunamiSource;
    private readonly IReadOnlyList<IRealtimeEarthquakeSource> _realtimeSources = [];
    private readonly IRealtimeObservationSource? _realtimeObservationSource;
    private readonly Func<WebSocketConnectionSettings, IStreamingEarthquakeSource>? _streamingSourceFactory;
    private IStreamingEarthquakeSource? _streamingSource;
    private readonly RefreshBackoffPolicy _refreshBackoffPolicy = new();
    private Task? _refreshLoopTask;
    private Task? _streamingLoopTask;
    private Task? _realtimeObservationLoopTask;
    private Task? _initializationTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _streamingSessionCancellation;
    private string _currentTime = string.Empty;
    private string _cacheStatus = "缓存：初始化中";
    private string _autoRefreshStatus = "自动刷新：未启动";
    private ImmutableArray<SourceStatus> _tsunamiSourceStatuses = [];
    private SourceStatus? _realtimeObservationStatus;
    private ApplicationSettings _applicationSettings;
    private bool _isTsunamiPageVisible;
    private bool _isInitialized;
    private bool _isDisposed;
    private bool _resourcesDisposed;

    public MainWindowViewModel(
        string? cachePath = null,
        bool enableNetwork = true,
        IStreamingEarthquakeSource? streamingSource = null,
        string? settingsPath = null,
        Func<WebSocketConnectionSettings, IStreamingEarthquakeSource>? streamingSourceFactory = null,
        IRealtimeTsunamiSource? tsunamiSource = null,
        IRealtimeObservationSource? realtimeObservationSource = null)
    {
        AppVersion = GetAppVersion();
        _settingsStore = new(settingsPath ?? GetDefaultSettingsPath());
        ApplicationSettingsLoadResult settingsLoad = _settingsStore.Load();
        _applicationSettings = settingsLoad.Settings;
        Settings = new(settingsLoad, ApplyWebSocketSettingsAsync, ImportLocalXmlAsync);
        JmaStationCoordinateCatalog stationCatalog = FixedJmaXmlDataLoader.LoadStationCatalog();
        _stationCatalog = stationCatalog;
        _tsunamiStationCatalog = FixedJmaXmlDataLoader.LoadTsunamiStationCatalog();
        _seedTsunamiReports = FixedJmaXmlDataLoader.LoadTsunamiReports();
        _seedReports = FixedJmaXmlDataLoader.LoadReports(stationCatalog);
        JmaIntensityRegionCatalog? regionCatalog = LoadRegionCatalog();
        _regionCatalog = regionCatalog;
        if (enableNetwork)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EarthquakeShow/0.41.0");
            JmaXmlEarthquakeSource xmlSource = new(
                _httpClient,
                stationCatalog: stationCatalog,
                regionCatalog: regionCatalog);
            P2pQuakeEarthquakeSource p2pQuakeSource = new(
                _httpClient,
                regionCatalog: regionCatalog,
                stationCatalog: stationCatalog);
            _realtimeSources = [xmlSource, p2pQuakeSource];
            _tsunamiSource = tsunamiSource ?? new JmaTsunamiXmlSource(_httpClient);
            _realtimeObservationSource = realtimeObservationSource ??
                new NtoolYahooRealtimeObservationSource(_httpClient);
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
            _realtimeObservationSource = realtimeObservationSource;
            _streamingSourceFactory = streamingSourceFactory;
            _streamingSource = streamingSource ??
                streamingSourceFactory?.Invoke(_applicationSettings.WebSocketSettings);
        }

        _repository = new SqliteEarthquakeEventRepository(
            cachePath ?? GetDefaultCachePath(),
            _realtimeSources,
            stationCatalog,
            regionCatalog);
        _tsunamiRepository = new SqliteTsunamiReportRepository(
            cachePath ?? GetDefaultCachePath(),
            _tsunamiSource);
        TsunamiPage = new TsunamiPageViewModel(_tsunamiRepository, _tsunamiStationCatalog);
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
                Path.Combine(mapRoot, "jma-earthquake-area-boundaries-medium.geojson"),
                Path.Combine(mapRoot, "jma-earthquake-areas.geojson"),
                Path.Combine(mapRoot, "jma-earthquake-municipalities.geojson")));
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

    public string TsunamiSourceStatusText => GetTsunamiSourceStatusText();

    public string RealtimeObservationStatusText => GetRealtimeObservationStatusText();

    public string AppVersion { get; }

    public EarthquakePageViewModel EarthquakePage { get; }

    public EarthquakeEventListViewModel EventList { get; }

    public EarthquakeMapViewModel Map { get; }

    public EarthquakeDetailsViewModel Details { get; }

    public WindowLayoutViewModel Layout { get; }

    public SettingsViewModel Settings { get; }

    public TsunamiPageViewModel TsunamiPage { get; }

    public bool IsTsunamiPageVisible => _isTsunamiPageVisible;

    public bool IsEarthquakePageVisible => !_isTsunamiPageVisible;

    public ImmutableArray<SourceStatus> TsunamiSourceStatuses => _tsunamiSourceStatuses;

    internal ImmutableArray<string> RealtimeSourceIds =>
        _realtimeSources.Select(source => source.SourceId).ToImmutableArray();

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
            Settings.SetLatestImport(
                await _repository.GetLatestLocalXmlImportAsync(token));
        await _tsunamiRepository.InitializeAsync(token);
        await _tsunamiRepository.SaveReportsAsync(_seedTsunamiReports, token);
        await _tsunamiRepository.SaveStationCatalogAsync(_tsunamiStationCatalog, token);
        UpdateTsunamiSourceStatuses();
        await TsunamiPage.LoadAsync(token);
        EarthquakePage.SetSourceState(
            _repository.SourceStatuses,
            isOffline: true);
        await EarthquakePage.LoadAsync(token);
        if (_realtimeSources.Count > 0 || _tsunamiSource is not null)
        {
            await RefreshFromNetworkAsync(token);
            _refreshLoopTask ??= RunRefreshLoopAsync(_lifetimeCancellation.Token);
        }

        if (_streamingSource is not null)
        {
            StartStreamingLoop();
        }

        if (_realtimeObservationSource is not null)
        {
            _realtimeObservationLoopTask ??= RunRealtimeObservationLoopAsync(
                _lifetimeCancellation.Token);
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
            await AwaitShutdownTaskAsync(_realtimeObservationLoopTask).ConfigureAwait(true);
            await _streamingRestartGate.WaitAsync().ConfigureAwait(true);
            _streamingRestartGate.Release();
        }
        finally
        {
            await DisposeResourcesAsync().ConfigureAwait(true);
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

    private async Task DisposeResourcesAsync()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        await EarthquakePage.DisposeAsync().ConfigureAwait(true);
        await EventList.DisposeAsync().ConfigureAwait(true);
        Details.Dispose();
        Map.Dispose();
        await TsunamiPage.DisposeAsync().ConfigureAwait(true);
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

    internal async Task<JmaXmlLocalFileImportResult> ImportLocalXmlAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        JmaXmlLocalFileImporter importer = new(
            stationCatalog: _stationCatalog,
            regionCatalog: _regionCatalog);
        JmaXmlLocalFileImportResult result = await _repository
            .ImportLocalXmlAsync(importer, directoryPath, cancellationToken)
            .ConfigureAwait(true);
        if (!_isDisposed)
        {
            CacheStatus = $"缓存：本地 XML 已导入 {result.SavedReportCount} 条报文";
        }

        return result;
    }

    public void ShowEarthquakePage()
    {
        if (!_isTsunamiPageVisible)
        {
            return;
        }

        _isTsunamiPageVisible = false;
        OnPropertyChanged(nameof(IsTsunamiPageVisible));
        OnPropertyChanged(nameof(IsEarthquakePageVisible));
    }

    public void ShowTsunamiPage()
    {
        if (_isTsunamiPageVisible)
        {
            return;
        }

        _isTsunamiPageVisible = true;
        OnPropertyChanged(nameof(IsTsunamiPageVisible));
        OnPropertyChanged(nameof(IsEarthquakePageVisible));
    }

    private async Task RefreshFromNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EarthquakePage.RefreshAsync(cancellationToken);
            if (_tsunamiSource is not null)
            {
                try
                {
                    await TsunamiPage.RefreshAsync(cancellationToken);
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
        OnPropertyChanged(nameof(TsunamiSourceStatusText));
    }

    private string GetTsunamiSourceStatusText()
    {
        SourceConnectionState? state = _tsunamiSourceStatuses
            .Select(status => (SourceConnectionState?)status.State)
            .OrderByDescending(GetTsunamiStatusPriority)
            .FirstOrDefault();
        return state switch
        {
            SourceConnectionState.Online => "海啸：在线",
            SourceConnectionState.Delayed => "海啸：延迟",
            SourceConnectionState.RateLimited => "海啸：限流",
            SourceConnectionState.ParseFailed => "海啸：解析失败",
            SourceConnectionState.Disconnected => "海啸：离线",
            SourceConnectionState.Disabled => "海啸：未启用",
            SourceConnectionState.Unknown => "海啸：状态未知",
            _ => "海啸：未启用",
        };
    }

    private string GetRealtimeObservationStatusText()
    {
        SourceStatus? status = _realtimeObservationStatus;
        return status?.State switch
        {
            SourceConnectionState.Online => "实时观测：在线",
            SourceConnectionState.Delayed => "实时观测：延迟",
            SourceConnectionState.RateLimited => "实时观测：限流",
            SourceConnectionState.ParseFailed => "实时观测：解析失败",
            SourceConnectionState.Disconnected => "实时观测：离线",
            SourceConnectionState.Disabled => "实时观测：未启用",
            _ => _realtimeObservationSource is null
                ? "实时观测：未启用"
                : "实时观测：等待数据",
        };
    }

    private async Task RunRealtimeObservationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                _realtimeObservationSource is not null)
            {
                RealtimeObservationFetchResult result = await _realtimeObservationSource
                    .FetchAsync(cancellationToken);
                _realtimeObservationStatus = result.Status;
                OnPropertyChanged(nameof(RealtimeObservationStatusText));
                if (!result.Stations.IsDefaultOrEmpty)
                {
                    Map.SetRealtimeObservationStations(result.Stations);
                }

                TimeSpan delay = result.Status.State switch
                {
                    SourceConnectionState.Online => TimeSpan.FromSeconds(1),
                    SourceConnectionState.Delayed => TimeSpan.FromSeconds(2),
                    SourceConnectionState.RateLimited => TimeSpan.FromSeconds(30),
                    _ => TimeSpan.FromSeconds(5),
                };
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!_isDisposed)
        {
            _realtimeObservationStatus = new SourceStatus(
                _realtimeObservationSource?.SourceId ?? "ntool-yahoo-realtime",
                SourceConnectionState.Disconnected,
                DateTimeOffset.UtcNow,
                Detail: $"实时观测循环已停止：{exception.Message}");
            OnPropertyChanged(nameof(RealtimeObservationStatusText));
        }
    }

    private static int GetTsunamiStatusPriority(SourceConnectionState? state) => state switch
    {
        SourceConnectionState.ParseFailed => 6,
        SourceConnectionState.RateLimited => 5,
        SourceConnectionState.Disconnected => 4,
        SourceConnectionState.Delayed => 3,
        SourceConnectionState.Unknown => 2,
        SourceConnectionState.Online => 1,
        SourceConnectionState.Disabled => 0,
        _ => -1,
    };

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = _refreshBackoffPolicy.GetNextDelay(
                    GetHttpRefreshSourceStatuses());
                AutoRefreshStatus = $"自动刷新：{FormatDelay(delay)} 后检查";
                await Task.Delay(delay, cancellationToken);
                AutoRefreshStatus = "自动刷新：检查中";
                try
                {
                    await RefreshFromNetworkAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    AutoRefreshStatus = $"自动刷新：本轮失败（{exception.Message}），稍后重试";
                }
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

    internal ImmutableArray<SourceStatus> GetHttpRefreshSourceStatuses()
    {
        return FilterRefreshSourceStatuses(
            EarthquakePage.State.SourceStatuses,
            _realtimeSources.Select(source => source.SourceId));
    }

    internal static ImmutableArray<SourceStatus> FilterRefreshSourceStatuses(
        IEnumerable<SourceStatus> statuses,
        IEnumerable<string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(sourceIds);
        HashSet<string> httpSourceIds = sourceIds.ToHashSet(StringComparer.Ordinal);
        return statuses
            .Where(status => httpSourceIds.Contains(status.SourceId))
            .ToImmutableArray();
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

    private IStreamingEarthquakeSource CreateStreamingSource(
        WebSocketConnectionSettings settings) =>
        new ReconnectingEarthquakeSource(
            new P2pQuakeWebSocketSource(
                keepAliveInterval: settings.KeepAliveInterval,
                stationCatalog: _stationCatalog),
            new StreamingReconnectPolicy(
                maxConnectionDuration: settings.MaxConnectionDuration));

    private static string FormatDelay(TimeSpan delay)
    {
        return delay.TotalMinutes >= 1
            ? $"{delay.TotalMinutes:0.#} 分钟"
            : $"{Math.Max(1, delay.TotalSeconds):0} 秒";
    }

    private static JmaIntensityRegionCatalog? LoadRegionCatalog()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Data",
            "Intensity",
            "jma-intensity-regions.json");
        return File.Exists(path) ? JmaIntensityRegionCatalog.LoadFile(path) : null;
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
