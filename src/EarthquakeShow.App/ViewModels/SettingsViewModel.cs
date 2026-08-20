using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.App.Services;

namespace EarthquakeShow.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Func<WebSocketConnectionSettings, Task> _applySettings;
    private int _keepAliveSeconds;
    private int _maxConnectionDurationMinutes;
    private WebSocketConnectionSettings _savedSettings;
    private bool _isApplying;
    private bool _isVisible;
    private string _statusText = string.Empty;

    public SettingsViewModel(
        ApplicationSettingsLoadResult loadResult,
        Func<WebSocketConnectionSettings, Task> applySettings)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
        _savedSettings = loadResult.Settings.WebSocketSettings;
        _keepAliveSeconds = _savedSettings.KeepAliveSeconds;
        _maxConnectionDurationMinutes = _savedSettings.MaxConnectionDurationMinutes;
        _statusText = loadResult.Warning ?? "连接策略设置已加载";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<int> KeepAliveOptions { get; } =
        Enumerable.Range(10, 111).ToArray();

    public IReadOnlyList<int> MaxConnectionDurationOptions { get; } =
        Enumerable.Range(1, 9).ToArray();

    public int KeepAliveSeconds
    {
        get => _keepAliveSeconds;
        set
        {
            if (_keepAliveSeconds == value)
            {
                return;
            }

            _keepAliveSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public int MaxConnectionDurationMinutes
    {
        get => _maxConnectionDurationMinutes;
        set
        {
            if (_maxConnectionDurationMinutes == value)
            {
                return;
            }

            _maxConnectionDurationMinutes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool IsDirty =>
        _keepAliveSeconds != _savedSettings.KeepAliveSeconds ||
        _maxConnectionDurationMinutes != _savedSettings.MaxConnectionDurationMinutes;

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (_isApplying == value)
            {
                return;
            }

            _isApplying = value;
            OnPropertyChanged();
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public void RestoreDefaults()
    {
        WebSocketConnectionSettings defaults = WebSocketConnectionSettings.Default;
        KeepAliveSeconds = defaults.KeepAliveSeconds;
        MaxConnectionDurationMinutes = defaults.MaxConnectionDurationMinutes;
        StatusText = "已恢复默认值，点击应用后生效";
    }

    public void ShowError(string message)
    {
        StatusText = message;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = new WebSocketConnectionSettings(
            KeepAliveSeconds,
            MaxConnectionDurationMinutes);
        settings.Validate();

        IsApplying = true;
        try
        {
            await _applySettings(settings).WaitAsync(cancellationToken)
                .ConfigureAwait(true);
            _savedSettings = settings;
            OnPropertyChanged(nameof(IsDirty));
            StatusText = "已保存连接策略设置";
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
