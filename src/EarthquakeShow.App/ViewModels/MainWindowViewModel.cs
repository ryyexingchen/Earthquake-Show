using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EarthquakeShow.App.Services;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Persistence;
using EarthquakeShow.Infrastructure.Sources;

namespace EarthquakeShow.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly DispatcherTimer _clockTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SqliteEarthquakeEventRepository _repository;
    private readonly IReadOnlyList<EarthquakeReport> _seedReports;
    private readonly HttpClient? _httpClient;
    private readonly JmaJsonEarthquakeSource? _realtimeSource;
    private string _currentTime = string.Empty;
    private string _cacheStatus = "缓存：初始化中";
    private bool _isDisposed;

    public MainWindowViewModel(
        string? cachePath = null,
        bool enableNetwork = true)
    {
        AppVersion = GetAppVersion();
        _seedReports = FixedJmaXmlDataLoader.LoadReports();
        if (enableNetwork)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EarthquakeShow/0.13.0");
            _realtimeSource = new JmaJsonEarthquakeSource(_httpClient);
        }

        _repository = new SqliteEarthquakeEventRepository(
            cachePath ?? GetDefaultCachePath(),
            _realtimeSource);
        EarthquakePage = new EarthquakePageViewModel(
            _repository);
        EventList = new EarthquakeEventListViewModel(EarthquakePage);
        Map = new EarthquakeMapViewModel(
            EarthquakePage,
            OfflineMapGeometry.LoadFromFile(
                Path.Combine(AppContext.BaseDirectory, "Assets", "japan-overview.geojson")));
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

    public string MapDataStatus => Map.IsOfficialBoundary ? "地图：离线边界" : "地图：离线示意";

    public string AppVersion { get; }

    public EarthquakePageViewModel EarthquakePage { get; }

    public EarthquakeEventListViewModel EventList { get; }

    public EarthquakeMapViewModel Map { get; }

    public EarthquakeDetailsViewModel Details { get; }

    public WindowLayoutViewModel Layout { get; }

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        CancellationToken token = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _lifetimeCancellation.Token;
        await _repository.InitializeAsync(_seedReports, token);
        CacheStatus = _repository.CacheStatus;
        EarthquakePage.SetSourceState(
            _repository.SourceStatuses,
            isOffline: true);
        await EarthquakePage.LoadAsync(token);
        if (_realtimeSource is not null)
        {
            await RefreshFromNetworkAsync(token);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetimeCancellation.Cancel();
        _httpClient?.Dispose();
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        Details.Dispose();
        Map.Dispose();
        EventList.Dispose();
        EarthquakePage.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task RefreshFromNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EarthquakePage.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
        DateTimeOffset japanTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, JapanTimeZone);
        CurrentTime = japanTime.ToString("yyyy-MM-dd HH:mm:ss 'JST'", CultureInfo.InvariantCulture);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
