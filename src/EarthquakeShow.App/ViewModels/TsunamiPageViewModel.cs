using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.App.ViewModels;

public sealed record TsunamiForecastAreaDisplay(
    string Name,
    string Code,
    TsunamiLevel Level,
    string LevelText,
    string ArrivalText,
    string HeightText);

public sealed record TsunamiObservationStationDisplay(
    string AreaName,
    string Name,
    string Code,
    string ArrivalText,
    string HeightText,
    string InitialText);

public sealed record TsunamiEstimationAreaDisplay(
    string Name,
    string Code,
    string ArrivalText,
    string HeightText);

public sealed record TsunamiInformationItemDisplay(
    string KindText,
    string CodeText,
    string LastKindText,
    string AreasText);

public sealed record TsunamiTimelineItemDisplay(
    string ReportCode,
    string StatusText,
    string ContextText,
    string IssuedAtText,
    TsunamiLevel Level,
    string LevelText,
    string StructureText,
    bool IsCancellation);

public sealed record TsunamiReportDifferenceDisplay(
    string FieldText,
    string PreviousText,
    string CurrentText);

public sealed class TsunamiPageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITsunamiReportRepository _repository;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TsunamiPageState _state = new();
    private string _rawXmlCopyStatus = string.Empty;
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
            OnPropertyChanged(nameof(SelectedReportLevel));
            OnPropertyChanged(nameof(SelectedReportLevelText));
            OnPropertyChanged(nameof(ForecastAreas));
            OnPropertyChanged(nameof(ObservationStations));
            OnPropertyChanged(nameof(EstimationAreas));
            OnPropertyChanged(nameof(HasForecastAreas));
            OnPropertyChanged(nameof(HasObservationStations));
            OnPropertyChanged(nameof(HasEstimationAreas));
            OnPropertyChanged(nameof(InformationItems));
            OnPropertyChanged(nameof(HasInformationItems));
            OnPropertyChanged(nameof(TimelineReports));
            OnPropertyChanged(nameof(HasTimelineReports));
            OnPropertyChanged(nameof(ReportDifferences));
            OnPropertyChanged(nameof(HasReportDifferences));
            OnPropertyChanged(nameof(ReportDifferenceStatusText));
            OnPropertyChanged(nameof(RawXmlText));
            OnPropertyChanged(nameof(HasRawXml));
            OnPropertyChanged(nameof(CanCopyRawXml));
            _rawXmlCopyStatus = string.Empty;
            OnPropertyChanged(nameof(RawXmlCopyStatus));
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

    public string SelectedReportStatusText => GetStatusText(State.SelectedReport?.Status);

    public string SelectedReportContextText => GetContextText(State.SelectedReport?.Context);

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

    public TsunamiLevel SelectedReportLevel => GetDisplayedReportLevel(State.SelectedReport);

    public string SelectedReportLevelText => GetReportLevelTextForDisplay(State.SelectedReport);

    public ImmutableArray<TsunamiForecastAreaDisplay> ForecastAreas =>
        State.SelectedReport is null
            ? []
            : State.SelectedReport.ForecastAreas
                .Select(CreateForecastAreaDisplay)
                .ToImmutableArray();

    public ImmutableArray<TsunamiObservationStationDisplay> ObservationStations =>
        State.SelectedReport is null
            ? []
            : State.SelectedReport.ObservationStations
                .Select(CreateObservationStationDisplay)
                .ToImmutableArray();

    public ImmutableArray<TsunamiEstimationAreaDisplay> EstimationAreas =>
        State.SelectedReport is null
            ? []
            : State.SelectedReport.EstimationAreas
                .Select(CreateEstimationAreaDisplay)
                .ToImmutableArray();

    public bool HasForecastAreas => !ForecastAreas.IsDefaultOrEmpty;

    public bool HasObservationStations => !ObservationStations.IsDefaultOrEmpty;

    public bool HasEstimationAreas => !EstimationAreas.IsDefaultOrEmpty;

    public ImmutableArray<TsunamiInformationItemDisplay> InformationItems =>
        State.SelectedReport is null
            ? []
            : State.SelectedReport.Items
                .Select(CreateInformationItemDisplay)
                .ToImmutableArray();

    public bool HasInformationItems => !InformationItems.IsDefaultOrEmpty;

    public ImmutableArray<TsunamiTimelineItemDisplay> TimelineReports =>
        State.SelectedReport is null
            ? []
            : State.Reports
                .Where(report => string.Equals(
                    report.EventId,
                    State.SelectedReport.EventId,
                    StringComparison.Ordinal))
                .OrderBy(report => report.IssuedAt)
                .ThenBy(report => report.ReceivedAt)
                .ThenBy(report => report.Source.SourceMessageId, StringComparer.Ordinal)
                .Select(report => CreateTimelineItemDisplay(
                    report))
                .ToImmutableArray();

    public bool HasTimelineReports => !TimelineReports.IsDefaultOrEmpty;

    public ImmutableArray<TsunamiReportDifferenceDisplay> ReportDifferences
    {
        get
        {
            JmaTsunamiReport? current = State.SelectedReport;
            JmaTsunamiReport? previous = FindPreviousReport(current);
            if (current is null || previous is null)
            {
                return [];
            }

            TsunamiReportDifferenceDisplay?[] candidates =
            [
                CreateDifference("报文代码", previous.ReportCode, current.ReportCode),
                CreateDifference("状态", GetStatusText(previous.Status), GetStatusText(current.Status)),
                CreateDifference("场景", GetContextText(previous.Context), GetContextText(current.Context)),
                CreateDifference("最高等级", GetReportLevelTextForDisplay(previous), GetReportLevelTextForDisplay(current)),
                CreateDifference("标题", FormatOptional(previous.HeadlineText), FormatOptional(current.HeadlineText)),
                CreateDifference("信息项", previous.Items.Length.ToString(), current.Items.Length.ToString()),
                CreateDifference("预报区", previous.ForecastAreas.Length.ToString(), current.ForecastAreas.Length.ToString()),
                CreateDifference("沿岸观测", previous.ObservationStations.Length.ToString(), current.ObservationStations.Length.ToString()),
                CreateDifference("近海推定", previous.EstimationAreas.Length.ToString(), current.EstimationAreas.Length.ToString()),
            ];
            return candidates
                .Where(item => item is not null)
                .Select(item => item!)
                .ToImmutableArray();
        }
    }

    public bool HasReportDifferences => !ReportDifferences.IsDefaultOrEmpty;

    public string ReportDifferenceStatusText
    {
        get
        {
            if (State.SelectedReport is null)
            {
                return string.Empty;
            }

            return FindPreviousReport(State.SelectedReport) is null
                ? "首报，没有上一报可比较"
                : HasReportDifferences
                    ? $"与上一报相比有 {ReportDifferences.Length} 项变化"
                    : "与上一报无字段差异";
        }
    }

    public string RawXmlText => State.SelectedReport?.Source.SourcePayload ?? string.Empty;

    public bool HasRawXml => !string.IsNullOrWhiteSpace(RawXmlText);

    public bool CanCopyRawXml => HasRawXml;

    public string RawXmlCopyStatus => _rawXmlCopyStatus;

    public string EmptyMessage => ShowError
        ? State.ErrorMessage ?? "海啸报文读取失败"
            : "本地缓存中没有海啸报文";

    public void MarkRawXmlCopied()
    {
        ThrowIfDisposed();
        if (!CanCopyRawXml)
        {
            return;
        }

        _rawXmlCopyStatus = "已复制原始 XML";
        OnPropertyChanged(nameof(RawXmlCopyStatus));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "未提供"
            : timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

    private static TsunamiForecastAreaDisplay CreateForecastAreaDisplay(
        JmaTsunamiForecastArea area)
    {
        TsunamiLevel level = JmaTsunamiClassifier.Classify(area.KindName, area.KindCode);
        return new(
            area.Name,
            area.Code,
            level,
            GetLevelText(level, area.KindName),
            FormatArrival(area.FirstArrivalTime, area.FirstArrivalCondition),
            FormatHeight(area.MaximumHeight));
    }

    private static TsunamiObservationStationDisplay CreateObservationStationDisplay(
        JmaTsunamiObservationStation station) => new(
            station.AreaName,
            station.Name,
            station.Code,
            FormatArrival(station.FirstArrivalTime, station.FirstArrivalCondition),
            FormatHeight(station.MaximumHeight),
            string.IsNullOrWhiteSpace(station.Initial) ? "未提供" : station.Initial!);

    private static TsunamiEstimationAreaDisplay CreateEstimationAreaDisplay(
        JmaTsunamiEstimationArea area) => new(
            area.Name,
            area.Code,
            FormatArrival(area.FirstArrivalTime, area.FirstArrivalCondition),
            FormatHeight(area.MaximumHeight));

    private static TsunamiInformationItemDisplay CreateInformationItemDisplay(
        JmaTsunamiInformationItem item) => new(
            string.IsNullOrWhiteSpace(item.KindName) ? "等级未提供" : item.KindName!,
            string.IsNullOrWhiteSpace(item.KindCode) ? "代码未提供" : item.KindCode!,
            string.IsNullOrWhiteSpace(item.LastKindName) ? "无上一状态" : item.LastKindName!,
            item.Areas.IsDefaultOrEmpty
                ? "无区域"
                : string.Join(
                    "、",
                    item.Areas.Select(area => $"{area.Name}（{area.Code}）")));

    private static TsunamiTimelineItemDisplay CreateTimelineItemDisplay(
        JmaTsunamiReport report)
    {
        bool isCancellation = report.Status == ReportStatus.Cancelled;
        TsunamiLevel level = isCancellation ? TsunamiLevel.Unknown : GetReportLevel(report);
        return new(
            report.ReportCode,
            isCancellation ? "解除" : GetStatusText(report.Status),
            GetContextText(report.Context),
            FormatTimestamp(report.IssuedAt),
            level,
            isCancellation ? "解除" : GetLevelText(level),
            $"预报区 {report.ForecastAreas.Length} · 沿岸观测 {report.ObservationStations.Length} · 推定区域 {report.EstimationAreas.Length}",
            isCancellation);
    }

    private static TsunamiReportDifferenceDisplay? CreateDifference(
        string fieldText,
        string previousText,
        string currentText) => string.Equals(previousText, currentText, StringComparison.Ordinal)
        ? null
        : new(fieldText, previousText, currentText);

    private static string FormatOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未提供" : value!;

    private static string GetStatusText(ReportStatus? status) => status switch
    {
        ReportStatus.Issued => "发布",
        ReportStatus.Correction => "订正",
        ReportStatus.Cancelled => "取消",
        _ => "状态不明",
    };

    private static string GetContextText(ReportContext? context) => context switch
    {
        ReportContext.Normal => "正式",
        ReportContext.Training => "训练",
        ReportContext.Test => "测试",
        _ => "不明",
    };

    private JmaTsunamiReport? FindPreviousReport(JmaTsunamiReport? current)
    {
        if (current is null)
        {
            return null;
        }

        JmaTsunamiReport[] reports = State.Reports
            .Where(report => string.Equals(report.EventId, current.EventId, StringComparison.Ordinal))
            .OrderBy(report => report.IssuedAt)
            .ThenBy(report => report.ReceivedAt)
            .ThenBy(report => report.Source.SourceMessageId, StringComparer.Ordinal)
            .ToArray();
        int currentIndex = Array.FindIndex(reports, report =>
            string.Equals(report.Source.SourceId, current.Source.SourceId, StringComparison.Ordinal) &&
            string.Equals(report.Source.SourceMessageId, current.Source.SourceMessageId, StringComparison.Ordinal));
        return currentIndex > 0 ? reports[currentIndex - 1] : null;
    }

    private static TsunamiLevel GetReportLevel(JmaTsunamiReport? report)
    {
        if (report is null)
        {
            return TsunamiLevel.Unknown;
        }

        return report.Items
            .Select(item => JmaTsunamiClassifier.Classify(item.KindName, item.KindCode))
            .Concat(report.ForecastAreas.Select(area =>
                JmaTsunamiClassifier.Classify(area.KindName, area.KindCode)))
            .OrderByDescending(GetLevelPriority)
            .FirstOrDefault(TsunamiLevel.Unknown);
    }

    private static TsunamiLevel GetDisplayedReportLevel(JmaTsunamiReport? report) =>
        report?.Status == ReportStatus.Cancelled
            ? TsunamiLevel.Unknown
            : GetReportLevel(report);

    private static string GetReportLevelTextForDisplay(JmaTsunamiReport? report) =>
        report?.Status == ReportStatus.Cancelled
            ? "解除"
            : GetLevelText(GetReportLevel(report));

    private static int GetLevelPriority(TsunamiLevel level) => level switch
    {
        TsunamiLevel.MajorWarning => 6,
        TsunamiLevel.Warning => 5,
        TsunamiLevel.Advisory => 4,
        TsunamiLevel.MinorChange => 3,
        TsunamiLevel.NoConcern => 2,
        TsunamiLevel.Investigating => 1,
        _ => 0,
    };

    private static string GetLevelText(
        TsunamiLevel level,
        string? originalText = null) =>
        level switch
        {
            TsunamiLevel.NoConcern => "津波の心配なし",
            TsunamiLevel.MinorChange => "若干の海面変動",
            TsunamiLevel.Advisory => "津波注意報",
            TsunamiLevel.Warning => "津波警報",
            TsunamiLevel.MajorWarning => "大津波警報",
            TsunamiLevel.Investigating => string.IsNullOrWhiteSpace(originalText)
                ? "津波 調査中"
                : originalText!,
            _ => string.IsNullOrWhiteSpace(originalText) ? "等级未提供" : originalText!,
        };

    private static string FormatArrival(
        DateTimeOffset? timestamp,
        string? condition) => timestamp is null
            ? string.IsNullOrWhiteSpace(condition) ? "未提供" : condition!
            : $"{FormatTimestamp(timestamp)}{(string.IsNullOrWhiteSpace(condition) ? string.Empty : $"（{condition}）")}";

    private static string FormatHeight(JmaTsunamiHeight? height)
    {
        if (height is null)
        {
            return "未提供";
        }

        if (height.Meters is double meters && double.IsFinite(meters))
        {
            string unit = string.IsNullOrWhiteSpace(height.Unit) ? "m" : height.Unit!;
            return $"{meters:0.##} {unit}";
        }

        return string.IsNullOrWhiteSpace(height.Description) ? "未提供" : height.Description!;
    }

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
