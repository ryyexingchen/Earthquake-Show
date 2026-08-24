using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed class TsunamiPageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITsunamiReportRepository _repository;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TsunamiPageState _state = new();
    private bool _isDisposed;

    public TsunamiPageViewModel(ITsunamiReportRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TsunamiPageState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Reports));
            OnPropertyChanged(nameof(HasReports));
            OnPropertyChanged(nameof(HasSelectedReport));
            OnPropertyChanged(nameof(SelectedReport));
            OnPropertyChanged(nameof(ResultCountText));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(SelectedReportStatusText));
            OnPropertyChanged(nameof(SelectedReportContextText));
            OnPropertyChanged(nameof(SelectedReportStatusContextText));
            OnPropertyChanged(nameof(SelectedReportInfoKindText));
            OnPropertyChanged(nameof(SelectedReportIssuedAtText));
            OnPropertyChanged(nameof(SelectedReportReceivedAtText));
            OnPropertyChanged(nameof(SelectedReportHeadlineText));
            OnPropertyChanged(nameof(SelectedReportIdentityText));
            OnPropertyChanged(nameof(SelectedReportStructureText));
        }
    }

    public ImmutableArray<JmaTsunamiReport> Reports => State.Reports;

    public bool HasReports => !State.Reports.IsDefaultOrEmpty;

    public bool HasSelectedReport => State.SelectedReport is not null;

    public JmaTsunamiReport? SelectedReport
    {
        get => State.SelectedReport;
        set
        {
            if (value is null)
            {
                ClearSelection();
                return;
            }

            SelectReport(
                value.EventId,
                value.Source.SourceId,
                value.Source.SourceMessageId);
        }
    }

    public string ResultCountText => $"{State.Reports.Length} 条";

    public bool IsLoading =>
        State.LoadState == TsunamiPageLoadState.Loading && State.Reports.IsDefaultOrEmpty;

    public bool ShowEmpty =>
        State.LoadState == TsunamiPageLoadState.Ready && State.Reports.IsDefaultOrEmpty;

    public bool ShowError =>
        State.LoadState == TsunamiPageLoadState.Error && State.Reports.IsDefaultOrEmpty;

    public bool CanRefresh => !State.IsRefreshing;

    public string SelectedReportStatusText => State.SelectedReport?.Status switch
    {
        ReportStatus.Issued => "发布",
        ReportStatus.Correction => "订正",
        ReportStatus.Cancelled => "取消",
        _ => "状态不明",
    };

    public string SelectedReportContextText => State.SelectedReport?.Context switch
    {
        ReportContext.Normal => "正式",
        ReportContext.Training => "训练",
        ReportContext.Test => "测试",
        _ => "不明",
    };

    public string SelectedReportStatusContextText =>
        $"{SelectedReportStatusText} · {SelectedReportContextText}";

    public string SelectedReportInfoKindText =>
        string.IsNullOrWhiteSpace(State.SelectedReport?.InfoKind)
            ? "海啸报文"
            : State.SelectedReport.InfoKind!;

    public string SelectedReportIssuedAtText => FormatTimestamp(State.SelectedReport?.IssuedAt);

    public string SelectedReportReceivedAtText => FormatTimestamp(State.SelectedReport?.ReceivedAt);

    public string SelectedReportHeadlineText =>
        string.IsNullOrWhiteSpace(State.SelectedReport?.HeadlineText)
            ? "无标题"
            : State.SelectedReport.HeadlineText!;

    public string SelectedReportIdentityText
    {
        get
        {
            JmaTsunamiReport? report = State.SelectedReport;
            return report is null
                ? ""
                : $"{report.EventId} · {report.Source.SourceId} · {report.Source.SourceMessageId}";
        }
    }

    public string SelectedReportStructureText
    {
        get
        {
            JmaTsunamiReport? report = State.SelectedReport;
            return report is null
                ? ""
                : $"预报区 {report.ForecastAreas.Length} · 沿岸观测 {report.ObservationStations.Length} · 推定区域 {report.EstimationAreas.Length}";
        }
    }

    public string EmptyMessage => ShowError
        ? State.ErrorMessage ?? "海啸报文读取失败"
        : "本地缓存中没有海啸报文";

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "未提供"
            : timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        State = State with
        {
            LoadState = TsunamiPageLoadState.Loading,
            ErrorMessage = null,
        };

        try
        {
            ImmutableArray<JmaTsunamiReport> reports =
                await _repository.ListReportsAsync(cancellationToken);
            ApplyReports(reports);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            State = State with
            {
                LoadState = TsunamiPageLoadState.Error,
                ErrorMessage = exception.Message,
            };
        }
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            State = State with
            {
                IsRefreshing = true,
                ErrorMessage = null,
            };

            try
            {
                await _repository.RefreshAsync(cancellationToken);
                ImmutableArray<JmaTsunamiReport> reports =
                    await _repository.ListReportsAsync(cancellationToken);
                ApplyReports(reports);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                State = State with
                {
                    LoadState = TsunamiPageLoadState.Error,
                    ErrorMessage = exception.Message,
                };
            }
        }
        finally
        {
            State = State with { IsRefreshing = false };
            _refreshGate.Release();
        }
    }

    public bool SelectReport(
        string eventId,
        string sourceId,
        string sourceMessageId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMessageId);
        JmaTsunamiReport? report = State.Reports.FirstOrDefault(item =>
            string.Equals(item.EventId, eventId, StringComparison.Ordinal) &&
            string.Equals(item.Source.SourceId, sourceId, StringComparison.Ordinal) &&
            string.Equals(item.Source.SourceMessageId, sourceMessageId, StringComparison.Ordinal));
        if (report is null)
        {
            return false;
        }

        State = State with { SelectedReport = report };
        return true;
    }

    public void ClearSelection()
    {
        ThrowIfDisposed();
        State = State with { SelectedReport = null };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _refreshGate.Dispose();
    }

    private void ApplyReports(ImmutableArray<JmaTsunamiReport> reports)
    {
        ImmutableArray<JmaTsunamiReport> ordered = reports
            .OrderByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.ReceivedAt)
            .ThenBy(report => report.EventId, StringComparer.Ordinal)
            .ThenBy(report => report.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(report => report.Source.SourceMessageId, StringComparer.Ordinal)
            .ToImmutableArray();
        JmaTsunamiReport? selected = FindSelectedReport(ordered) ?? ordered.FirstOrDefault();
        ImmutableArray<SourceStatus> statuses = _repository.SourceStatuses;
        State = State with
        {
            Reports = ordered,
            SelectedReport = selected,
            SourceStatuses = statuses,
            LoadState = TsunamiPageLoadState.Ready,
            IsOffline = statuses.IsDefaultOrEmpty ||
                statuses.All(status => status.State != SourceConnectionState.Online),
            ErrorMessage = null,
        };
    }

    private JmaTsunamiReport? FindSelectedReport(
        ImmutableArray<JmaTsunamiReport> reports)
    {
        JmaTsunamiReport? current = State.SelectedReport;
        if (current is null)
        {
            return null;
        }

        return reports.FirstOrDefault(report =>
            string.Equals(report.EventId, current.EventId, StringComparison.Ordinal) &&
            string.Equals(report.Source.SourceId, current.Source.SourceId, StringComparison.Ordinal) &&
            string.Equals(report.Source.SourceMessageId, current.Source.SourceMessageId, StringComparison.Ordinal));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
