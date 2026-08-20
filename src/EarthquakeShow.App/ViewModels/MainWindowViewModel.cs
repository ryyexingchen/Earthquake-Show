using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly DispatcherTimer _clockTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private string _currentTime = string.Empty;
    private bool _isDisposed;

    public MainWindowViewModel()
    {
        AppVersion = GetAppVersion();
        EarthquakePage = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository());
        EventList = new EarthquakeEventListViewModel(EarthquakePage);
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

    public string CacheStatus => "缓存：未初始化";

    public string MapDataStatus => "地图：未安装";

    public string AppVersion { get; }

    public EarthquakePageViewModel EarthquakePage { get; }

    public EarthquakeEventListViewModel EventList { get; }

    public WindowLayoutViewModel Layout { get; }

    public ValueTask InitializeAsync()
    {
        return EarthquakePage.LoadAsync(_lifetimeCancellation.Token);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetimeCancellation.Cancel();
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        EventList.Dispose();
        EarthquakePage.Dispose();
        _lifetimeCancellation.Dispose();
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
