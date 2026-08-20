using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.App.ViewModels;

public sealed record EarthquakeDetailField(string Label, string Value);

public sealed record EarthquakeSourceDifferenceItemViewModel(
    string SourceId,
    string SourceMessageId,
    string DifferenceText,
    string PriorityText);

public sealed record EarthquakeEventAssociationItemViewModel(
    string EventId,
    string SourceId,
    string SourceMessageId,
    string ConfidenceText,
    string MatchText);

public sealed record EarthquakeObservationItemViewModel(
    string Kind,
    string Name,
    string Code,
    string ParentText,
    JmaIntensity Intensity,
    string IntensityText,
    GeoCoordinate? Coordinate)
{
    public string LocationText => Coordinate is null ? "位置未知" : "单击定位";
}

public sealed record EarthquakeTimelineItemViewModel(
    string SourceId,
    string SourceMessageId,
    string ReportTypeText,
    string SerialText,
    string IssuedAtText,
    string ReceivedAtText,
    string StatusText,
    string ChangeSummary,
    bool IsSelected);

public sealed class EarthquakeDetailsViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly EarthquakePageViewModel _page;
    private readonly EarthquakeMapViewModel _map;
    private string _observationSearchText = string.Empty;
    private bool _showHighestOnly;
    private EarthquakeObservationItemViewModel? _selectedObservation;
    private EarthquakeTimelineItemViewModel? _selectedTimelineItem;
    private IReadOnlyList<EarthquakeObservationItemViewModel> _allObservations = [];
    private bool _isDisposed;

    public EarthquakeDetailsViewModel(
        EarthquakePageViewModel page,
        EarthquakeMapViewModel map)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _page.PropertyChanged += OnPagePropertyChanged;
        Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasReport => _page.State.ViewedReport is not null;

    public bool ShowEmptyState => !HasReport;

    public string Title { get; private set; } = "未选择事件";

    public string SnapshotText { get; private set; } = "请选择一个地震事件";

    public IReadOnlyList<EarthquakeDetailField> SummaryFields { get; private set; } = [];

    public IReadOnlyList<EarthquakeSourceDifferenceItemViewModel> SourceDifferences { get; private set; } = [];

    public bool HasSourceDifferences => SourceDifferences.Count > 0;

    public IReadOnlyList<EarthquakeEventAssociationItemViewModel> EventAssociations { get; private set; } = [];

    public bool HasEventAssociations => EventAssociations.Count > 0;

    public IReadOnlyList<EarthquakeObservationItemViewModel> Observations { get; private set; } = [];

    public IReadOnlyList<EarthquakeTimelineItemViewModel> TimelineItems { get; private set; } = [];

    public string RawPayload { get; private set; } = "无原始数据";

    public string RawMetadataText { get; private set; } = "未选择报文";

    public string ObservationCountText => $"{Observations.Count} 条";

    public string ObservationSearchText
    {
        get => _observationSearchText;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (_observationSearchText == normalized)
            {
                return;
            }

            _observationSearchText = normalized;
            RebuildVisibleObservations();
            OnPropertyChanged();
        }
    }

    public bool ShowHighestOnly
    {
        get => _showHighestOnly;
        set
        {
            if (_showHighestOnly == value)
            {
                return;
            }

            _showHighestOnly = value;
            RebuildVisibleObservations();
            OnPropertyChanged();
        }
    }

    public EarthquakeObservationItemViewModel? SelectedObservation
    {
        get => _selectedObservation;
        set
        {
            if (_selectedObservation == value)
            {
                return;
            }

            _selectedObservation = value;
            OnPropertyChanged();
            if (value?.Coordinate is GeoCoordinate coordinate)
            {
                _map.FocusLocation(coordinate);
            }
        }
    }

    public EarthquakeTimelineItemViewModel? SelectedTimelineItem
    {
        get => _selectedTimelineItem;
        set
        {
            if (_selectedTimelineItem == value)
            {
                return;
            }

            _selectedTimelineItem = value;
            OnPropertyChanged();
            if (value is not null)
            {
                _page.SelectReport(value.SourceId, value.SourceMessageId);
            }
        }
    }

    public bool CanGoPrevious => GetViewedReportIndex() > 0;

    public bool CanGoNext
    {
        get
        {
            int index = GetViewedReportIndex();
            return index >= 0 && index < (_page.State.SelectedEvent?.Reports.Length ?? 0) - 1;
        }
    }

    public bool CanReturnToLatest => CanGoNext;

    public bool CanLocateHypocenter =>
        _page.State.ViewedReport?.Hypocenter?.Coordinate is not null;

    public void GoPreviousReport()
    {
        SelectRelativeReport(-1);
    }

    public void GoNextReport()
    {
        SelectRelativeReport(1);
    }

    public void ReturnToLatestReport()
    {
        _page.ReturnToLatestReport();
    }

    public void FocusHypocenter()
    {
        if (_page.State.ViewedReport?.Hypocenter?.Coordinate is GeoCoordinate coordinate)
        {
            _map.FocusLocation(coordinate);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _page.PropertyChanged -= OnPagePropertyChanged;
        _isDisposed = true;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(EarthquakePageViewModel.State))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        EarthquakeEvent? earthquakeEvent = _page.State.SelectedEvent;
        if (report is null || earthquakeEvent is null)
        {
            Title = "未选择事件";
            SnapshotText = "请选择一个地震事件";
            SummaryFields = [];
            SourceDifferences = [];
            EventAssociations = [];
            _allObservations = [];
            Observations = [];
            TimelineItems = [];
            RawPayload = "无原始数据";
            RawMetadataText = "未选择报文";
            _selectedObservation = null;
            _selectedTimelineItem = null;
            RaiseAllProperties();
            return;
        }

        int viewedIndex = GetViewedReportIndex();
        Title = report.Hypocenter?.Name ?? "震源不明";
        SnapshotText = $"第 {viewedIndex + 1} / {earthquakeEvent.Reports.Length} 报 · " +
            $"{GetReportTypeText(report)} · {GetStatusText(report.Status)}";
        SummaryFields = BuildSummaryFields(earthquakeEvent.EventId, report);
        SourceDifferences = BuildSourceDifferences(earthquakeEvent, report);
        EventAssociations = BuildEventAssociations(earthquakeEvent);
        _allObservations = BuildObservations(report);
        RebuildVisibleObservations();
        TimelineItems = BuildTimeline(earthquakeEvent, report);
        _selectedTimelineItem = TimelineItems.FirstOrDefault(item => item.IsSelected);
        RawPayload = string.IsNullOrWhiteSpace(report.Source.SourcePayload)
            ? "无原始数据"
            : report.Source.SourcePayload;
        RawMetadataText = $"{report.ReportCode} · {report.Source.SourceId} · " +
            $"{report.Source.SourceMessageId}";
        _selectedObservation = null;
        RaiseAllProperties();
    }

    private static IReadOnlyList<EarthquakeDetailField> BuildSummaryFields(
        string eventId,
        EarthquakeReport report)
    {
        GeoCoordinate? coordinate = report.Hypocenter?.Coordinate;
        string coordinateText = coordinate is GeoCoordinate value
            ? $"{value.Latitude:0.0000}, {value.Longitude:0.0000}"
            : "不明";
        string magnitudeText = report.Magnitude?.Value is double magnitude
            ? $"M {magnitude:0.0}" + (string.IsNullOrWhiteSpace(report.Magnitude.Type)
                ? string.Empty
                : $" ({report.Magnitude.Type})")
            : "不明";

        return
        [
            new("事件 ID", eventId),
            new("最大震度", GetIntensityText(report.MaxIntensity)),
            new("震源", report.Hypocenter?.Name ?? "不明"),
            new("发生时间", FormatTime(report.OriginTime)),
            new("发布时间", FormatTime(report.IssuedAt)),
            new("接收时间", FormatTime(report.ReceivedAt)),
            new("经纬度", coordinateText),
            new("深度", report.Hypocenter?.DepthKm is int depth ? $"{depth} km" : "不明"),
            new("震级", magnitudeText),
            new("海啸", report.TsunamiComment ?? "不明"),
            new("报文", $"{report.ReportCode} · {GetReportTypeText(report)}"),
            new("状态", $"{GetStatusText(report.Status)} · {GetContextText(report.Context)}"),
            new("来源", report.Source.SourceId),
        ];
    }

    private static IReadOnlyList<EarthquakeSourceDifferenceItemViewModel> BuildSourceDifferences(
        EarthquakeEvent earthquakeEvent,
        EarthquakeReport selectedReport)
    {
        return earthquakeEvent.Reports
            .Where(report => !IsSameSource(report.Source, selectedReport.Source))
            .GroupBy(report => report.Source.SourceId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(report => report.IssuedAt)
                .ThenByDescending(report => report.ReceivedAt)
                .First())
            .Select(report => new EarthquakeSourceDifferenceItemViewModel(
                report.Source.SourceId,
                report.Source.SourceMessageId,
                GetSourceDifferenceText(selectedReport, report),
                GetSourcePriorityText(report.Source.SourceId)))
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetSourceDifferenceText(
        EarthquakeReport selected,
        EarthquakeReport other)
    {
        var changes = new List<string>();
        AddChange(changes, "状态", GetStatusText(selected.Status), GetStatusText(other.Status));
        AddChange(changes, "震度", GetIntensityText(selected.MaxIntensity), GetIntensityText(other.MaxIntensity));
        AddChange(changes, "震级", GetMagnitudeText(selected), GetMagnitudeText(other));
        AddChange(changes, "震源", selected.Hypocenter?.Name ?? "不明", other.Hypocenter?.Name ?? "不明");
        AddChange(changes, "深度", GetDepthText(selected), GetDepthText(other));
        AddChange(changes, "观测点", selected.IntensityStations.Length.ToString(), other.IntensityStations.Length.ToString());
        return changes.Count == 0 ? "无字段差异" : string.Join("；", changes);
    }

    private static string GetDepthText(EarthquakeReport report)
    {
        return report.Hypocenter?.DepthKm is int depth ? $"{depth} km" : "不明";
    }

    private static string GetSourcePriorityText(string sourceId)
    {
        return sourceId switch
        {
            "jma-xml" => "JMA XML 优先",
            "jma-json" => "JMA JSON 摘要",
            "p2pquake" => "第三方补充",
            _ => "其他来源",
        };
    }

    private IReadOnlyList<EarthquakeEventAssociationItemViewModel> BuildEventAssociations(
        EarthquakeEvent selectedEvent)
    {
        return EarthquakeEventAssociator.Associate(_page.State.Events)
            .Where(association =>
                string.Equals(association.LeftEventId, selectedEvent.EventId, StringComparison.Ordinal) ||
                string.Equals(association.RightEventId, selectedEvent.EventId, StringComparison.Ordinal))
            .Select(association =>
            {
                bool isLeft = string.Equals(
                    association.LeftEventId,
                    selectedEvent.EventId,
                    StringComparison.Ordinal);
                return new EarthquakeEventAssociationItemViewModel(
                    isLeft ? association.RightEventId : association.LeftEventId,
                    isLeft ? association.RightSourceId : association.LeftSourceId,
                    isLeft ? association.RightSourceMessageId : association.LeftSourceMessageId,
                    association.Confidence == EarthquakeAssociationConfidence.High
                        ? "高置信度"
                        : "中置信度",
                    $"时间差 {association.TimeDifferenceSeconds:0.#} 秒 · " +
                    $"距离 {association.DistanceKm:0.#} km" +
                    (association.MagnitudeDifference is double magnitudeDifference
                        ? $" · 震级差 {magnitudeDifference:0.0}"
                        : " · 震级差不明"));
            })
            .OrderByDescending(item => item.ConfidenceText, StringComparer.Ordinal)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<EarthquakeObservationItemViewModel> BuildObservations(
        EarthquakeReport report)
    {
        var items = new List<EarthquakeObservationItemViewModel>();
        items.AddRange(report.IntensityAreas.Select(area => new EarthquakeObservationItemViewModel(
            "区域",
            area.Name,
            area.Code,
            area.PrefectureName,
            area.MaxIntensity,
            GetIntensityText(area.MaxIntensity),
            null)));
        items.AddRange(report.IntensityMunicipalities.Select(item => new EarthquakeObservationItemViewModel(
            "市町村",
            item.Name,
            item.Code,
            item.AreaCode,
            item.MaxIntensity,
            GetIntensityText(item.MaxIntensity),
            null)));
        items.AddRange(report.IntensityStations.Select(station => new EarthquakeObservationItemViewModel(
            "观测点",
            station.Name,
            station.Code,
            station.MunicipalityCode,
            station.Intensity,
            GetIntensityText(station.Intensity),
            station.Coordinate)));
        return items
            .OrderBy(item => item.Intensity == JmaIntensity.Unknown)
            .ThenByDescending(item => item.Intensity)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<EarthquakeTimelineItemViewModel> BuildTimeline(
        EarthquakeEvent earthquakeEvent,
        EarthquakeReport selectedReport)
    {
        var items = new List<EarthquakeTimelineItemViewModel>();
        EarthquakeReport? previous = null;
        foreach (EarthquakeReport report in earthquakeEvent.Reports)
        {
            items.Add(new EarthquakeTimelineItemViewModel(
                report.Source.SourceId,
                report.Source.SourceMessageId,
                GetReportTypeText(report),
                report.Serial is int serial ? $"第 {serial} 报" : "无报次",
                $"发布 {FormatTime(report.IssuedAt)}",
                $"接收 {FormatTime(report.ReceivedAt)}",
                GetStatusText(report.Status),
                GetChangeSummary(previous, report),
                IsSameSource(report.Source, selectedReport.Source)));
            previous = report;
        }

        return items;
    }

    private void RebuildVisibleObservations()
    {
        IEnumerable<EarthquakeObservationItemViewModel> query = _allObservations;
        if (_observationSearchText.Length > 0)
        {
            query = query.Where(item =>
                Contains(item.Name, _observationSearchText) ||
                Contains(item.Code, _observationSearchText) ||
                Contains(item.ParentText, _observationSearchText));
        }

        if (_showHighestOnly && _allObservations.Count > 0)
        {
            JmaIntensity highest = _allObservations
                .Where(item => item.Intensity != JmaIntensity.Unknown)
                .Select(item => item.Intensity)
                .DefaultIfEmpty(JmaIntensity.Unknown)
                .Max();
            query = query.Where(item => item.Intensity == highest);
        }

        Observations = query.ToArray();
        _selectedObservation = null;
        OnPropertyChanged(nameof(Observations));
        OnPropertyChanged(nameof(ObservationCountText));
        OnPropertyChanged(nameof(SelectedObservation));
    }

    private void SelectRelativeReport(int offset)
    {
        EarthquakeEvent? earthquakeEvent = _page.State.SelectedEvent;
        int index = GetViewedReportIndex();
        int targetIndex = index + offset;
        if (earthquakeEvent is null || targetIndex < 0 || targetIndex >= earthquakeEvent.Reports.Length)
        {
            return;
        }

        EarthquakeReport target = earthquakeEvent.Reports[targetIndex];
        _page.SelectReport(target.Source.SourceId, target.Source.SourceMessageId);
    }

    private int GetViewedReportIndex()
    {
        EarthquakeEvent? earthquakeEvent = _page.State.SelectedEvent;
        SourceReference? source = _page.State.ViewedReport?.Source;
        if (earthquakeEvent is null || source is null)
        {
            return -1;
        }

        for (int index = 0; index < earthquakeEvent.Reports.Length; index++)
        {
            if (IsSameSource(earthquakeEvent.Reports[index].Source, source))
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetChangeSummary(EarthquakeReport? previous, EarthquakeReport current)
    {
        if (previous is null)
        {
            return "首报";
        }

        var changes = new List<string>();
        AddChange(changes, "状态", GetStatusText(previous.Status), GetStatusText(current.Status));
        AddChange(changes, "震度", GetIntensityText(previous.MaxIntensity), GetIntensityText(current.MaxIntensity));
        AddChange(changes, "震级", GetMagnitudeText(previous), GetMagnitudeText(current));
        AddChange(changes, "震源", previous.Hypocenter?.Name ?? "不明", current.Hypocenter?.Name ?? "不明");
        return changes.Count == 0 ? "无关键字段变化" : string.Join("；", changes);
    }

    private static void AddChange(List<string> changes, string label, string previous, string current)
    {
        if (!string.Equals(previous, current, StringComparison.Ordinal))
        {
            changes.Add($"{label} {previous} → {current}");
        }
    }

    private static string GetMagnitudeText(EarthquakeReport report)
    {
        return report.Magnitude?.Value is double value ? $"M {value:0.0}" : "不明";
    }

    private static bool IsSameSource(SourceReference left, SourceReference right)
    {
        return string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal) &&
            string.Equals(left.SourceMessageId, right.SourceMessageId, StringComparison.Ordinal);
    }

    private static string GetReportTypeText(EarthquakeReport report)
    {
        return report.ReportType switch
        {
            EarthquakeReportType.SeismicIntensity => "震度速報",
            EarthquakeReportType.Hypocenter => "震源情报",
            EarthquakeReportType.HypocenterAndIntensity => "震源・震度情报",
            _ => report.ReportCode,
        };
    }

    private static string GetStatusText(ReportStatus status)
    {
        return status switch
        {
            ReportStatus.Issued => "发布",
            ReportStatus.Correction => "订正",
            ReportStatus.Cancelled => "取消",
            _ => "状态不明",
        };
    }

    private static string GetContextText(ReportContext context)
    {
        return context switch
        {
            ReportContext.Normal => "正常",
            ReportContext.Training => "训练",
            ReportContext.Test => "测试",
            _ => "上下文不明",
        };
    }

    private static string GetIntensityText(JmaIntensity intensity)
    {
        return intensity switch
        {
            JmaIntensity.One => "1",
            JmaIntensity.Two => "2",
            JmaIntensity.Three => "3",
            JmaIntensity.Four => "4",
            JmaIntensity.FiveLower => "5弱",
            JmaIntensity.FiveUpper => "5强",
            JmaIntensity.SixLower => "6弱",
            JmaIntensity.SixUpper => "6强",
            JmaIntensity.Seven => "7",
            _ => "不明",
        };
    }

    private static string FormatTime(DateTimeOffset? value)
    {
        return value is DateTimeOffset time
            ? TimeZoneInfo.ConvertTime(time, JapanTimeZone).ToString("yyyy-MM-dd HH:mm:ss 'JST'")
            : "不明";
    }

    private static bool Contains(string value, string searchText)
    {
        return value.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseAllProperties()
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SnapshotText));
        OnPropertyChanged(nameof(SummaryFields));
        OnPropertyChanged(nameof(SourceDifferences));
        OnPropertyChanged(nameof(HasSourceDifferences));
        OnPropertyChanged(nameof(EventAssociations));
        OnPropertyChanged(nameof(HasEventAssociations));
        OnPropertyChanged(nameof(Observations));
        OnPropertyChanged(nameof(TimelineItems));
        OnPropertyChanged(nameof(RawPayload));
        OnPropertyChanged(nameof(RawMetadataText));
        OnPropertyChanged(nameof(ObservationCountText));
        OnPropertyChanged(nameof(SelectedObservation));
        OnPropertyChanged(nameof(SelectedTimelineItem));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanReturnToLatest));
        OnPropertyChanged(nameof(CanLocateHypocenter));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
