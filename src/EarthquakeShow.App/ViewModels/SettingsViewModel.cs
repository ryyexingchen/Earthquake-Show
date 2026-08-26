using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.App.Services;
using EarthquakeShow.Infrastructure.Sources;

namespace EarthquakeShow.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Func<WebSocketConnectionSettings, Task> _applySettings;
    private readonly Func<string, CancellationToken, Task<JmaXmlLocalFileImportResult>>? _importLocalXml;
    private int _keepAliveSeconds;
    private int _maxConnectionDurationMinutes;
    private WebSocketConnectionSettings _savedSettings;
    private bool _isApplying;
    private bool _isImporting;
    private bool _isVisible;
    private string _statusText = string.Empty;

    public SettingsViewModel(
        ApplicationSettingsLoadResult loadResult,
        Func<WebSocketConnectionSettings, Task> applySettings,
        Func<string, CancellationToken, Task<JmaXmlLocalFileImportResult>>? importLocalXml = null)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
        _importLocalXml = importLocalXml;
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
            OnPropertyChanged(nameof(CanImportLocalXml));
        }
    }

    public bool CanImportLocalXml => _importLocalXml is not null && !IsApplying && !IsImporting;

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (_isImporting == value)
            {
                return;
            }

            _isImporting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanImportLocalXml));
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

    public async Task ImportLocalXmlAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (_importLocalXml is null)
        {
            throw new InvalidOperationException("当前宿主未配置本地 XML 导入功能。");
        }

        IsImporting = true;
        StatusText = "正在导入本地 JMA XML…";
        try
        {
            JmaXmlLocalFileImportResult result = await _importLocalXml(
                directoryPath,
                cancellationToken).ConfigureAwait(true);
            StatusText = $"本地 XML 导入完成：写入/更新 {result.SavedReportCount} 条，跳过 {result.SkippedFiles.Length} 个，失败 {result.Failures.Length} 个";
        }
        finally
        {
            IsImporting = false;
        }
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
