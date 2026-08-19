using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace EarthquakeShow.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly DispatcherTimer _clockTimer;
    private string _currentTime = string.Empty;

    public MainWindowViewModel()
    {
        AppVersion = GetAppVersion();
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

    public string NetworkStatus => "网络未连接";

    public string CacheStatus => "缓存未初始化";

    public string AppVersion { get; }

    public void Dispose()
    {
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
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
