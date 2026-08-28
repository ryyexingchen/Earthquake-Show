using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.App.ViewModels;

public sealed record EarthquakeDetailField(string Label, string Value)
{
    public bool IsOverviewField => Label is "最大震度" or "震级" or "震源地" or "深度" or "海啸" or "震源・规模";
}

public sealed record EarthquakeIntensityDisplayViewModel(string Text, string Kind);

public sealed record EarthquakeSummaryOverviewViewModel(
    EarthquakeIntensityDisplayViewModel? MaximumIntensity,
    string MagnitudeText,
    bool HasMagnitude,
    string HypocenterText,
    bool HasHypocenter,
    string DepthText,
    bool HasDepth,
    bool HasSourceScaleInvestigation,
    EarthquakeTsunamiStatusViewModel TsunamiStatus)
{
    public bool HasMaximumIntensity => MaximumIntensity is not null;

    public bool HasSourceDetails => HasMagnitude || HasHypocenter || HasDepth;
}

public sealed record EarthquakeTsunamiStatusViewModel(string Text, string Kind);

public sealed record EarthquakeTimelineSummaryViewModel(
    EarthquakeIntensityDisplayViewModel? MaximumIntensity,
    string MagnitudeText,
    bool HasMagnitude,
    string HypocenterText,
    bool HasHypocenter,
    string DepthText,
    bool HasDepth,
    bool HasSourceScaleInvestigation,
    EarthquakeTsunamiStatusViewModel TsunamiStatus)
{
    public bool HasMaximumIntensity => MaximumIntensity is not null;

    public bool HasSourceDetails => HasMagnitude || HasHypocenter || HasDepth;
}

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

public sealed record EarthquakeObservationTreeNode(
    string Kind,
    string Name,
    string Code,
    string ParentText,
    JmaIntensity Intensity,
    string IntensityText,
    GeoCoordinate? Coordinate,
    EarthquakeObservationItemViewModel? Observation,
    ImmutableArray<EarthquakeObservationTreeNode> Children)
{
    public string IntensityKind => Intensity.ToString();

    public string LocationText => Observation?.LocationText ?? "--";

    public string ChildCountText => Children.Length > 0 ? $"{Children.Length} 项" : string.Empty;

    public bool IsLeaf => Children.IsDefaultOrEmpty;

    public EarthquakeObservationTreeNode WithChildren(
        IEnumerable<EarthquakeObservationTreeNode> children)
    {
        return this with { Children = children.ToImmutableArray() };
    }

    public static EarthquakeObservationTreeNode FromObservation(
        EarthquakeObservationItemViewModel observation)
    {
        return new(
            observation.Kind,
            observation.Name,
            observation.Code,
            observation.ParentText,
            observation.Intensity,
            observation.IntensityText,
            observation.Coordinate,
            observation,
            []);
    }

    public static EarthquakeObservationTreeNode CreateUnmapped(
        IEnumerable<EarthquakeObservationTreeNode> children)
    {
        return new(
            "未映射",
            "未映射",
            string.Empty,
            string.Empty,
            JmaIntensity.Unknown,
            "不明",
            null,
            null,
            children.ToImmutableArray());
    }
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
    bool IsSelected,
    EarthquakeTimelineSummaryViewModel Summary);

public sealed class EarthquakeDetailsViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private readonly EarthquakePageViewModel _page;
    private readonly EarthquakeMapViewModel _map;
    private string _observationSearchText = string.Empty;
    private bool _showHighestOnly;
    private EarthquakeObservationItemViewModel? _selectedObservation;
    private EarthquakeObservationTreeNode? _selectedObservationNode;
    private EarthquakeTimelineItemViewModel? _selectedTimelineItem;
    private EarthquakeEvent? _lastSelectedEvent;
    private EarthquakeReport? _lastViewedReport;
    private IReadOnlyList<EarthquakeObservationItemViewModel> _allObservations = [];
    private IReadOnlyList<EarthquakeObservationTreeNode> _allObservationTreeNodes = [];
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

    public EarthquakeTsunamiStatusViewModel TsunamiStatus { get; private set; } =
        new("津波 调查中", "Investigating");

    public EarthquakeSummaryOverviewViewModel SummaryOverview { get; private set; } =
        new(null, string.Empty, false, string.Empty, false, string.Empty, false, false,
            new EarthquakeTsunamiStatusViewModel("津波 调查中", "Investigating"));

    public IReadOnlyList<EarthquakeSourceDifferenceItemViewModel> SourceDifferences { get; private set; } = [];

    public bool HasSourceDifferences => SourceDifferences.Count > 0;

    public IReadOnlyList<EarthquakeEventAssociationItemViewModel> EventAssociations { get; private set; } = [];

    public bool HasEventAssociations => EventAssociations.Count > 0;

    public bool CanToggleSource => GetAvailableSourceIds().Count > 1;

    public string SourceToggleText => CanToggleSource
        ? $"切换到 {GetSourceDisplayName(GetOtherSourceId())}"
        : "切换来源";

    public IReadOnlyList<EarthquakeObservationItemViewModel> Observations { get; private set; } = [];

    public IReadOnlyList<EarthquakeObservationTreeNode> ObservationTreeNodes { get; private set; } = [];

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
            _map.SelectObservation(value?.Kind, value?.Code, value?.Coordinate);
            if (value?.Coordinate is GeoCoordinate coordinate)
            {
                _map.FocusLocation(coordinate);
            }
        }
    }

    public EarthquakeObservationTreeNode? SelectedObservationNode
    {
        get => _selectedObservationNode;
        private set
        {
            if (_selectedObservationNode == value)
            {
                return;
            }

            _selectedObservationNode = value;
            OnPropertyChanged();
            SelectedObservation = value?.Observation;
        }
    }

    public void SelectObservationNode(EarthquakeObservationTreeNode? node)
    {
        SelectedObservationNode = node;
    }

    public bool IsObservationNodeSelected(EarthquakeObservationTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Equals(SelectedObservationNode, node);
    }

    public void ToggleObservationNode(EarthquakeObservationTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        SelectObservationNode(IsObservationNodeSelected(node) ? null : node);
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

    public bool CanGoPrevious
    {
        get
        {
            EarthquakeReport[] sourceReports = GetViewedSourceReports();
            return GetViewedSourceReportIndex(sourceReports) > 0;
        }
    }

    public bool CanGoNext
    {
        get
        {
            EarthquakeReport[] sourceReports = GetViewedSourceReports();
            int index = GetViewedSourceReportIndex(sourceReports);
            return index >= 0 && index < sourceReports.Length - 1;
        }
    }

    public bool CanReturnToLatest => CanGoNext;

    public bool CanLocateHypocenter =>
        GetViewedHypocenter()?.Coordinate is not null;

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
        EarthquakeReport? target = GetViewedSourceReports().LastOrDefault();
        if (target is not null)
        {
            _page.SelectReport(target.Source.SourceId, target.Source.SourceMessageId);
        }
    }

    public void ToggleSource()
    {
        EarthquakeEvent? selectedEvent = _page.State.SelectedEvent;
        EarthquakeReport? viewedReport = _page.State.ViewedReport;
        if (selectedEvent is null || viewedReport is null)
        {
            return;
        }

        string otherSourceId = GetOtherSourceId();
        if (otherSourceId.Length == 0)
        {
            return;
        }

        EarthquakeReport? target = selectedEvent.Reports
            .Where(report => string.Equals(
                report.Source.SourceId,
                otherSourceId,
                StringComparison.Ordinal))
            .OrderByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.ReceivedAt)
            .FirstOrDefault();
        if (target is not null)
        {
            _page.SelectReport(target.Source.SourceId, target.Source.SourceMessageId);
        }
    }

    public void FocusHypocenter()
    {
        if (GetViewedHypocenter()?.Coordinate is GeoCoordinate coordinate)
        {
            _map.FocusLocation(coordinate);
        }
    }

    private Hypocenter? GetViewedHypocenter()
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        if (report is null)
        {
            return null;
        }

        if (report.Hypocenter is Hypocenter hypocenter)
        {
            return hypocenter;
        }

        if (report.ReportType != EarthquakeReportType.SeismicIntensity)
        {
            return null;
        }

        int viewedIndex = GetViewedReportIndex();
        if (viewedIndex < 0 || _page.State.SelectedEvent is not EarthquakeEvent selectedEvent)
        {
            return null;
        }

        return selectedEvent.Reports
            .Take(viewedIndex + 1)
            .LastOrDefault(item => item.Hypocenter is not null)
            ?.Hypocenter;
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
            EarthquakePageState state = _page.State;
            if (!ReferenceEquals(_lastSelectedEvent, state.SelectedEvent) ||
                !ReferenceEquals(_lastViewedReport, state.ViewedReport))
            {
                Rebuild();
            }
        }
    }

    private void Rebuild()
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        EarthquakeEvent? earthquakeEvent = _page.State.SelectedEvent;
        _lastSelectedEvent = earthquakeEvent;
        _lastViewedReport = report;
        if (report is null || earthquakeEvent is null)
        {
            Title = "未选择事件";
            SnapshotText = "请选择一个地震事件";
            SummaryFields = [];
            TsunamiStatus = new EarthquakeTsunamiStatusViewModel("津波 调查中", "Investigating");
            SummaryOverview = new(
                null,
                string.Empty,
                false,
                string.Empty,
                false,
                string.Empty,
                false,
                false,
                TsunamiStatus);
            SourceDifferences = [];
            EventAssociations = [];
            _allObservations = [];
            _allObservationTreeNodes = [];
            Observations = [];
            ObservationTreeNodes = [];
            TimelineItems = [];
            RawPayload = "无原始数据";
            RawMetadataText = "未选择报文";
            _selectedObservation = null;
            _selectedTimelineItem = null;
            RaiseAllProperties();
            return;
        }

        int viewedIndex = GetViewedReportIndex();
        EarthquakeReport[] viewedSourceReports = GetViewedSourceReports();
        int viewedSourceIndex = GetViewedSourceReportIndex(viewedSourceReports);
        ReportDisplaySnapshot displaySnapshot = BuildDisplaySnapshot(
            earthquakeEvent.Reports.Take(viewedIndex + 1));
        Title = displaySnapshot.Hypocenter?.Name ?? earthquakeEvent.EventId;
        SnapshotText = $"第 {viewedSourceIndex + 1} / {viewedSourceReports.Length} 报 · " +
            $"{GetReportTypeText(report)} · {GetStatusText(report.Status)}";
        SummaryFields = BuildSummaryFields(earthquakeEvent.EventId, report, displaySnapshot);
        TsunamiStatus = BuildTsunamiStatus(
            displaySnapshot.TsunamiComment,
            displaySnapshot.TsunamiCommentCode);
        SummaryOverview = BuildSummaryOverview(report.ReportType, displaySnapshot);
        SourceDifferences = BuildSourceDifferences(earthquakeEvent, report);
        EventAssociations = BuildEventAssociations(earthquakeEvent);
        _allObservations = BuildObservations(GetObservationReport(earthquakeEvent, report));
        _allObservationTreeNodes = BuildObservationTree(_allObservations);
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

    private static EarthquakeReport GetObservationReport(
        EarthquakeEvent earthquakeEvent,
        EarthquakeReport report)
    {
        int viewedIndex = earthquakeEvent.Reports
            .Select((item, index) => (item, index))
            .FirstOrDefault(item =>
                string.Equals(item.item.Source.SourceId, report.Source.SourceId, StringComparison.Ordinal) &&
                string.Equals(item.item.Source.SourceMessageId, report.Source.SourceMessageId, StringComparison.Ordinal))
            .index;
        IEnumerable<EarthquakeReport> reports = earthquakeEvent.Reports.Take(viewedIndex + 1);
        EarthquakeReport? areaReport = reports.LastOrDefault(item => !item.IntensityAreas.IsDefaultOrEmpty);
        EarthquakeReport? municipalityReport = reports.LastOrDefault(item => !item.IntensityMunicipalities.IsDefaultOrEmpty);
        EarthquakeReport? stationReport = reports.LastOrDefault(item => !item.IntensityStations.IsDefaultOrEmpty);
        EarthquakeReport? intensityReport = reports.LastOrDefault(item => item.MaxIntensity != JmaIntensity.Unknown);

        return report with
        {
            MaxIntensity = report.MaxIntensity == JmaIntensity.Unknown
                ? intensityReport?.MaxIntensity ?? JmaIntensity.Unknown
                : report.MaxIntensity,
            IntensityAreas = report.IntensityAreas.IsDefaultOrEmpty
                ? areaReport?.IntensityAreas ?? []
                : report.IntensityAreas,
            IntensityMunicipalities = report.IntensityMunicipalities.IsDefaultOrEmpty
                ? municipalityReport?.IntensityMunicipalities ?? []
                : report.IntensityMunicipalities,
            IntensityStations = report.IntensityStations.IsDefaultOrEmpty
                ? stationReport?.IntensityStations ?? []
                : report.IntensityStations,
        };
    }

    private static IReadOnlyList<EarthquakeDetailField> BuildSummaryFields(
        string eventId,
        EarthquakeReport report,
        ReportDisplaySnapshot displaySnapshot)
    {
        var fields = new List<EarthquakeDetailField>
        {
            new("事件 ID", eventId),
        };

        if (displaySnapshot.MaxIntensity is JmaIntensity maxIntensity)
        {
            fields.Add(new("最大震度", GetIntensityText(maxIntensity)));
        }

        if (!string.IsNullOrWhiteSpace(displaySnapshot.Hypocenter?.Name))
        {
            fields.Add(new("震源地", displaySnapshot.Hypocenter.Name));
        }

        if (displaySnapshot.Magnitude?.Value is double magnitude)
        {
            fields.Add(new(
                "震级",
                $"M {magnitude:0.0}" +
                (string.IsNullOrWhiteSpace(displaySnapshot.Magnitude.Type)
                    ? string.Empty
                    : $" ({displaySnapshot.Magnitude.Type})")));
        }

        if (displaySnapshot.Hypocenter?.DepthKm is int depth)
        {
            fields.Add(new("深度", $"{depth} km"));
        }

        if (report.ReportType == EarthquakeReportType.SeismicIntensity &&
            displaySnapshot.Hypocenter is null &&
            displaySnapshot.Magnitude is null)
        {
            fields.Add(new("震源・规模", "调查中"));
        }

        fields.Add(new(
            "海啸",
            BuildTsunamiStatus(
                displaySnapshot.TsunamiComment,
                displaySnapshot.TsunamiCommentCode).Text));

        if (displaySnapshot.Hypocenter?.Coordinate is GeoCoordinate coordinate)
        {
            fields.Add(new(
                "经纬度",
                $"{coordinate.Latitude:0.0000}, {coordinate.Longitude:0.0000}"));
        }

        if (displaySnapshot.OriginTime is DateTimeOffset originTime)
        {
            fields.Add(new("发生时间", FormatTime(originTime)));
        }

        fields.Add(new("发布时间", FormatTime(report.IssuedAt)));
        fields.Add(new("接收时间", FormatTime(report.ReceivedAt)));
        fields.Add(new("报文", $"{report.ReportCode} · {GetReportTypeText(report)}"));
        fields.Add(new("状态", $"{GetStatusText(report.Status)} · {GetContextText(report.Context)}"));
        fields.Add(new("来源", report.Source.SourceId));
        return fields;
    }

    private static EarthquakeSummaryOverviewViewModel BuildSummaryOverview(
        EarthquakeReportType reportType,
        ReportDisplaySnapshot snapshot)
    {
        EarthquakeTsunamiStatusViewModel tsunamiStatus = BuildTsunamiStatus(
            snapshot.TsunamiComment,
            snapshot.TsunamiCommentCode);
        EarthquakeIntensityDisplayViewModel? maximumIntensity = snapshot.MaxIntensity is JmaIntensity intensity
            ? new(GetIntensityText(intensity), GetIntensityKind(intensity))
            : null;
        string magnitudeText = snapshot.Magnitude?.Value is double magnitude
            ? $"M {magnitude:0.0}" +
                (string.IsNullOrWhiteSpace(snapshot.Magnitude.Type)
                    ? string.Empty
                    : $" ({snapshot.Magnitude.Type})")
            : string.Empty;
        string hypocenterText = snapshot.Hypocenter?.Name ?? string.Empty;
        string depthText = snapshot.Hypocenter?.DepthKm is int depth
            ? $"{depth} km"
            : string.Empty;
        bool hasSourceScaleInvestigation = reportType == EarthquakeReportType.SeismicIntensity &&
            snapshot.Hypocenter is null &&
            snapshot.Magnitude is null;
        return new(
            maximumIntensity,
            magnitudeText,
            magnitudeText.Length > 0,
            hypocenterText,
            hypocenterText.Length > 0,
            depthText,
            depthText.Length > 0,
            hasSourceScaleInvestigation,
            tsunamiStatus);
    }

    private static ReportDisplaySnapshot BuildDisplaySnapshot(
        IEnumerable<EarthquakeReport> reports)
    {
        var snapshot = new ReportDisplaySnapshot();
        foreach (EarthquakeReport report in reports)
        {
            if (report.Status != ReportStatus.Unknown)
            {
                snapshot.Status = report.Status;
            }

            if (report.MaxIntensity != JmaIntensity.Unknown)
            {
                snapshot.MaxIntensity = report.MaxIntensity;
            }

            if (report.Hypocenter is not null)
            {
                snapshot.Hypocenter = report.Hypocenter;
            }

            if (report.Magnitude is not null)
            {
                snapshot.Magnitude = report.Magnitude;
            }

            if (report.OriginTime is DateTimeOffset originTime)
            {
                snapshot.OriginTime = originTime;
            }

            if (!string.IsNullOrWhiteSpace(report.TsunamiComment))
            {
                snapshot.TsunamiComment = report.TsunamiComment;
                snapshot.TsunamiCommentCode = report.TsunamiCommentCode;
            }
        }

        return snapshot;
    }

    private static EarthquakeTsunamiStatusViewModel BuildTsunamiStatus(
        string? comment,
        string? code = null)
    {
        TsunamiLevel level = JmaTsunamiClassifier.Classify(comment, code);
        if (level == TsunamiLevel.Investigating &&
            (string.IsNullOrWhiteSpace(comment) || JmaTsunamiClassifier.IsGenericTemplate(comment)))
        {
            return new("津波 调查中", "Investigating");
        }

        // JMA 的“津波警报等（大津波警报・津波警报あるいは津波注意報）”是通用模板，
        // 不能从括号内的枚举文本推断当前实际等级。
        return level switch
        {
            TsunamiLevel.NoConcern => new("津波の心配なし", "NoConcern"),
            TsunamiLevel.MinorChange => new("若干の海面変動", "MinorChange"),
            TsunamiLevel.Advisory => new("津波注意報", "Advisory"),
            TsunamiLevel.Warning => new("津波警報", "Warning"),
            TsunamiLevel.MajorWarning => new("大津波警報", "MajorWarning"),
            _ => new(string.IsNullOrWhiteSpace(comment) ? "津波 调查中" : comment.Trim(), "Investigating"),
        };
    }

    private sealed class ReportDisplaySnapshot
    {
        public ReportStatus? Status { get; set; }

        public JmaIntensity? MaxIntensity { get; set; }

        public Hypocenter? Hypocenter { get; set; }

        public Magnitude? Magnitude { get; set; }

        public DateTimeOffset? OriginTime { get; set; }

        public string? TsunamiComment { get; set; }

        public string? TsunamiCommentCode { get; set; }
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

    private IReadOnlyList<string> GetAvailableSourceIds()
    {
        return _page.State.SelectedEvent?.Reports
            .Select(report => report.Source.SourceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GetSourcePriority)
            .ThenBy(sourceId => sourceId, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private string GetOtherSourceId()
    {
        string currentSourceId = _page.State.ViewedReport?.Source.SourceId ?? string.Empty;
        return GetAvailableSourceIds()
            .FirstOrDefault(sourceId => !string.Equals(
                sourceId,
                currentSourceId,
                StringComparison.Ordinal)) ?? string.Empty;
    }

    private static int GetSourcePriority(string sourceId)
    {
        return sourceId switch
        {
            "p2pquake" => 5,
            "jma-xml" => 20,
            _ => 10,
        };
    }

    private static string GetSourceDisplayName(string sourceId)
    {
        return sourceId switch
        {
            "jma-xml" => "JMA XML",
            "p2pquake" => "P2PQuake",
            _ => sourceId,
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

    private IReadOnlyList<EarthquakeObservationItemViewModel> BuildObservations(
        EarthquakeReport report)
    {
        var items = new List<EarthquakeObservationItemViewModel>();
        items.AddRange(report.IntensityAreas
            .GroupBy(area => (area.PrefectureCode, area.PrefectureName))
            .Select(group => new EarthquakeObservationItemViewModel(
                "都道府县",
                group.Key.PrefectureName,
                group.Key.PrefectureCode,
                string.Empty,
                group.Select(item => item.MaxIntensity).Aggregate(MaxIntensity),
                GetIntensityText(group.Select(item => item.MaxIntensity).Aggregate(MaxIntensity)),
                null)));
        items.AddRange(report.IntensityAreas.Select(area => new EarthquakeObservationItemViewModel(
            "区域",
            area.Name,
            area.Code,
            area.PrefectureCode,
            area.MaxIntensity,
            GetIntensityText(area.MaxIntensity),
            _map.TryGetAreaFocusCoordinate(area.Code, out GeoCoordinate coordinate)
                ? coordinate
                : null)));
        items.AddRange(report.IntensityMunicipalities.Select(item => new EarthquakeObservationItemViewModel(
            "市町村",
            item.Name,
            item.Code,
            item.AreaCode,
            item.MaxIntensity,
            GetIntensityText(item.MaxIntensity),
            _map.TryGetMunicipalityFocusCoordinate(item.Code, out GeoCoordinate coordinate)
                ? coordinate
                : null)));
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

    private static IReadOnlyList<EarthquakeObservationTreeNode> BuildObservationTree(
        IReadOnlyList<EarthquakeObservationItemViewModel> observations)
    {
        var prefectureNodes = new Dictionary<string, EarthquakeObservationTreeNode>(StringComparer.Ordinal);
        var prefectureChildren = new Dictionary<string, List<EarthquakeObservationTreeNode>>(StringComparer.Ordinal);
        var areaNodes = new Dictionary<string, EarthquakeObservationTreeNode>(StringComparer.Ordinal);
        var areaChildren = new Dictionary<string, List<EarthquakeObservationTreeNode>>(StringComparer.Ordinal);
        var municipalityNodes = new Dictionary<string, EarthquakeObservationTreeNode>(StringComparer.Ordinal);
        var municipalityChildren = new Dictionary<string, List<EarthquakeObservationTreeNode>>(StringComparer.Ordinal);
        var unmappedNodes = new List<EarthquakeObservationTreeNode>();
        var stationCodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (EarthquakeObservationItemViewModel observation in observations.Where(item => item.Kind == "都道府县"))
        {
            EarthquakeObservationTreeNode node = EarthquakeObservationTreeNode.FromObservation(observation);
            if (string.IsNullOrWhiteSpace(observation.Code) ||
                !prefectureNodes.TryAdd(observation.Code, node))
            {
                unmappedNodes.Add(node);
                continue;
            }

            prefectureChildren[observation.Code] = [];
        }

        foreach (EarthquakeObservationItemViewModel observation in observations.Where(item => item.Kind == "区域"))
        {
            EarthquakeObservationTreeNode node = EarthquakeObservationTreeNode.FromObservation(observation);
            if (string.IsNullOrWhiteSpace(observation.Code) ||
                !prefectureNodes.ContainsKey(observation.ParentText) ||
                !areaNodes.TryAdd(observation.Code, node))
            {
                unmappedNodes.Add(node);
                continue;
            }

            areaChildren[observation.Code] = [];
            prefectureChildren[observation.ParentText].Add(node);
        }

        foreach (EarthquakeObservationItemViewModel observation in observations.Where(item => item.Kind == "市町村"))
        {
            EarthquakeObservationTreeNode node = EarthquakeObservationTreeNode.FromObservation(observation);
            if (string.IsNullOrWhiteSpace(observation.Code) ||
                !areaNodes.ContainsKey(observation.ParentText) ||
                !municipalityNodes.TryAdd(observation.Code, node))
            {
                unmappedNodes.Add(node);
                continue;
            }

            municipalityChildren[observation.Code] = [];
            areaChildren[observation.ParentText].Add(node);
        }

        foreach (EarthquakeObservationItemViewModel observation in observations.Where(item => item.Kind == "观测点"))
        {
            EarthquakeObservationTreeNode node = EarthquakeObservationTreeNode.FromObservation(observation);
            if (string.IsNullOrWhiteSpace(observation.Code) ||
                !municipalityNodes.ContainsKey(observation.ParentText) ||
                !stationCodes.Add(observation.Code))
            {
                unmappedNodes.Add(node);
                continue;
            }

            municipalityChildren[observation.ParentText].Add(node);
        }

        var roots = new List<EarthquakeObservationTreeNode>();
        foreach (EarthquakeObservationTreeNode prefecture in prefectureNodes.Values)
        {
            var areas = prefectureChildren[prefecture.Code]
                .Select(area => area.WithChildren(
                    areaChildren[area.Code]
                        .Select(municipality => municipality.WithChildren(
                            municipalityChildren[municipality.Code]
                                .OrderByDescending(item => item.Intensity)
                                .ThenBy(item => item.Name, StringComparer.Ordinal)))
                        .OrderByDescending(item => item.Intensity)
                        .ThenBy(item => item.Name, StringComparer.Ordinal)))
                .OrderByDescending(item => item.Intensity)
                .ThenBy(item => item.Name, StringComparer.Ordinal);
            roots.Add(prefecture.WithChildren(areas));
        }

        if (unmappedNodes.Count > 0)
        {
            roots.Add(EarthquakeObservationTreeNode.CreateUnmapped(unmappedNodes));
        }

        return roots
            .OrderByDescending(item => item.Kind == "未映射" ? JmaIntensity.Unknown : item.Intensity)
            .ThenBy(item => item.Kind == "未映射" ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<EarthquakeTimelineItemViewModel> BuildTimeline(
        EarthquakeEvent earthquakeEvent,
        EarthquakeReport selectedReport)
    {
        var items = new List<EarthquakeTimelineItemViewModel>();
        ReportDisplaySnapshot previousSnapshot = new();
        bool hasPrevious = false;
        EarthquakeReport[] sourceReports = earthquakeEvent.Reports
            .Where(report => string.Equals(
                report.Source.SourceId,
                selectedReport.Source.SourceId,
                StringComparison.Ordinal))
            .ToArray();
        for (int index = 0; index < sourceReports.Length; index++)
        {
            EarthquakeReport report = sourceReports[index];
            ReportDisplaySnapshot currentSnapshot = BuildDisplaySnapshot(
                sourceReports.Take(index + 1));
            items.Add(new EarthquakeTimelineItemViewModel(
                report.Source.SourceId,
                report.Source.SourceMessageId,
                GetReportTypeText(report),
                report.Serial is int serial ? $"第 {serial} 报" : "无报次",
                $"发布 {FormatTime(report.IssuedAt)}",
                $"接收 {FormatTime(report.ReceivedAt)}",
                GetStatusText(report.Status),
                GetChangeSummary(
                    hasPrevious ? previousSnapshot : null,
                    report,
                    currentSnapshot),
                IsSameSource(report.Source, selectedReport.Source),
                BuildTimelineSummary(
                    hasPrevious ? previousSnapshot : null,
                    report,
                    currentSnapshot)));
            previousSnapshot = currentSnapshot;
            hasPrevious = true;
        }

        return items;
    }

    private void RebuildVisibleObservations()
    {
        JmaIntensity? highest = null;

        if (_showHighestOnly && _allObservations.Count > 0)
        {
            highest = _allObservations
                .Where(item => item.Intensity != JmaIntensity.Unknown)
                .Select(item => item.Intensity)
                .DefaultIfEmpty(JmaIntensity.Unknown)
                .Max();
        }

        ObservationTreeNodes = _allObservationTreeNodes
            .Select(node => FilterObservationTree(node, highest))
            .Where(node => node is not null)
            .Select(node => node!)
            .ToArray();
        Observations = ObservationTreeNodes
            .SelectMany(FlattenObservationNodes)
            .Where(node => node.Observation is not null)
            .Select(node => node.Observation!)
            .ToArray();
        _selectedObservation = null;
        _selectedObservationNode = null;
        _map.ClearSelectedObservation();
        OnPropertyChanged(nameof(Observations));
        OnPropertyChanged(nameof(ObservationTreeNodes));
        OnPropertyChanged(nameof(ObservationCountText));
        OnPropertyChanged(nameof(SelectedObservation));
        OnPropertyChanged(nameof(SelectedObservationNode));
    }

    private static IEnumerable<EarthquakeObservationTreeNode> FlattenObservationNodes(
        EarthquakeObservationTreeNode node)
    {
        yield return node;
        foreach (EarthquakeObservationTreeNode child in node.Children.SelectMany(FlattenObservationNodes))
        {
            yield return child;
        }
    }

    private EarthquakeObservationTreeNode? FilterObservationTree(
        EarthquakeObservationTreeNode node,
        JmaIntensity? highest)
    {
        bool matchesSearch = _observationSearchText.Length == 0 ||
            Contains(node.Name, _observationSearchText) ||
            Contains(node.Code, _observationSearchText) ||
            Contains(node.ParentText, _observationSearchText) ||
            Contains(node.Kind, _observationSearchText);

        EarthquakeObservationTreeNode[] children = node.Children
            .Select(child => FilterObservationTree(child, highest))
            .Where(child => child is not null)
            .Select(child => child!)
            .ToArray();

        if (node.IsLeaf)
        {
            bool matchesIntensity = highest is null ||
                node.Observation is null ||
                node.Intensity == highest.Value;
            return matchesSearch && matchesIntensity ? node : null;
        }

        if (matchesSearch && highest is null)
        {
            return node;
        }

        return children.Length > 0 ? node.WithChildren(children) : null;
    }

    private void SelectRelativeReport(int offset)
    {
        EarthquakeReport[] sourceReports = GetViewedSourceReports();
        int index = GetViewedSourceReportIndex(sourceReports);
        int targetIndex = index + offset;
        if (targetIndex < 0 || targetIndex >= sourceReports.Length)
        {
            return;
        }

        EarthquakeReport target = sourceReports[targetIndex];
        _page.SelectReport(target.Source.SourceId, target.Source.SourceMessageId);
    }

    private EarthquakeReport[] GetViewedSourceReports()
    {
        EarthquakeEvent? earthquakeEvent = _page.State.SelectedEvent;
        string? sourceId = _page.State.ViewedReport?.Source.SourceId;
        return earthquakeEvent is null || sourceId is null
            ? []
            : earthquakeEvent.Reports
                .Where(report => string.Equals(
                    report.Source.SourceId,
                    sourceId,
                    StringComparison.Ordinal))
                .ToArray();
    }

    private int GetViewedSourceReportIndex(EarthquakeReport[] sourceReports)
    {
        SourceReference? source = _page.State.ViewedReport?.Source;
        if (source is null)
        {
            return -1;
        }

        for (int index = 0; index < sourceReports.Length; index++)
        {
            if (IsSameSource(sourceReports[index].Source, source))
            {
                return index;
            }
        }

        return -1;
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

    private static string GetChangeSummary(
        ReportDisplaySnapshot? previous,
        EarthquakeReport current,
        ReportDisplaySnapshot currentSnapshot)
    {
        var fields = new List<string>();
        if (previous?.Status is ReportStatus previousStatus &&
            current.Status != ReportStatus.Unknown)
        {
            AddKnownChange(
                fields,
                "状态",
                GetStatusText(previousStatus),
                GetStatusText(current.Status));
        }

        AddCumulativeField(
            fields,
            "最大震度",
            previous?.MaxIntensity is JmaIntensity previousIntensity
                ? GetIntensityText(previousIntensity)
                : null,
            currentSnapshot.MaxIntensity is JmaIntensity intensity
                ? GetIntensityText(intensity)
                : null,
            current.MaxIntensity != JmaIntensity.Unknown);

        AddCumulativeField(
            fields,
            "震级",
            previous?.Magnitude is Magnitude previousMagnitude
                ? GetMagnitudeText(previousMagnitude)
                : null,
            currentSnapshot.Magnitude is Magnitude magnitude
                ? GetMagnitudeText(magnitude)
                : null,
            current.Magnitude is not null);

        AddCumulativeField(
            fields,
            "震源地",
            previous?.Hypocenter?.Name,
            currentSnapshot.Hypocenter?.Name,
            !string.IsNullOrWhiteSpace(current.Hypocenter?.Name));

        AddCumulativeField(
            fields,
            "深度",
            previous?.Hypocenter?.DepthKm is int previousDepth
                ? $"{previousDepth} km"
                : null,
            currentSnapshot.Hypocenter?.DepthKm is int depth
                ? $"{depth} km"
                : null,
            current.Hypocenter?.DepthKm is not null);

        AddCumulativeField(
            fields,
            "海啸",
            previous?.TsunamiComment is string previousTsunami
                ? BuildTsunamiStatus(previousTsunami, previous?.TsunamiCommentCode).Text
                : null,
            BuildTsunamiStatus(
                currentSnapshot.TsunamiComment,
                currentSnapshot.TsunamiCommentCode).Text,
            !string.IsNullOrWhiteSpace(current.TsunamiComment));

        if (current.ReportType == EarthquakeReportType.SeismicIntensity &&
            currentSnapshot.Hypocenter is null &&
            currentSnapshot.Magnitude is null)
        {
            fields.Add("震源・规模：调查中");
        }

        return fields.Count == 0 ? "无关键字段变化" : string.Join("；", fields);
    }

    private static EarthquakeTimelineSummaryViewModel BuildTimelineSummary(
        ReportDisplaySnapshot? previous,
        EarthquakeReport current,
        ReportDisplaySnapshot currentSnapshot)
    {
        string? maximumIntensityText = GetCumulativeFieldText(
            previous?.MaxIntensity is JmaIntensity previousIntensity
                ? GetIntensityText(previousIntensity)
                : null,
            currentSnapshot.MaxIntensity is JmaIntensity intensity
                ? GetIntensityText(intensity)
                : null,
            current.MaxIntensity != JmaIntensity.Unknown);
        EarthquakeIntensityDisplayViewModel? maximumIntensity =
            currentSnapshot.MaxIntensity is JmaIntensity currentIntensity && maximumIntensityText is not null
                ? new(maximumIntensityText, GetIntensityKind(currentIntensity))
                : null;

        string? magnitudeText = GetCumulativeFieldText(
            previous?.Magnitude is Magnitude previousMagnitude
                ? GetMagnitudeText(previousMagnitude)
                : null,
            currentSnapshot.Magnitude is Magnitude magnitude
                ? GetMagnitudeText(magnitude)
                : null,
            current.Magnitude is not null);
        string? hypocenterText = GetCumulativeFieldText(
            previous?.Hypocenter?.Name,
            currentSnapshot.Hypocenter?.Name,
            !string.IsNullOrWhiteSpace(current.Hypocenter?.Name));
        string? depthText = GetCumulativeFieldText(
            previous?.Hypocenter?.DepthKm is int previousDepth
                ? $"{previousDepth} km"
                : null,
            currentSnapshot.Hypocenter?.DepthKm is int depth
                ? $"{depth} km"
                : null,
            current.Hypocenter?.DepthKm is not null);

        EarthquakeTsunamiStatusViewModel currentTsunami =
            BuildTsunamiStatus(
                currentSnapshot.TsunamiComment,
                currentSnapshot.TsunamiCommentCode);
        string? previousTsunami = previous?.TsunamiComment is string previousComment
            ? BuildTsunamiStatus(previousComment, previous?.TsunamiCommentCode).Text
            : null;
        string tsunamiText = GetCumulativeFieldText(
            previousTsunami,
            currentTsunami.Text,
            !string.IsNullOrWhiteSpace(current.TsunamiComment)) ?? currentTsunami.Text;
        bool hasSourceScaleInvestigation = current.ReportType == EarthquakeReportType.SeismicIntensity &&
            currentSnapshot.Hypocenter is null &&
            currentSnapshot.Magnitude is null;

        return new(
            maximumIntensity,
            magnitudeText ?? string.Empty,
            magnitudeText is not null,
            hypocenterText ?? string.Empty,
            hypocenterText is not null,
            depthText ?? string.Empty,
            depthText is not null,
            hasSourceScaleInvestigation,
            currentTsunami with { Text = tsunamiText });
    }

    private static string? GetCumulativeFieldText(
        string? previous,
        string? current,
        bool currentProvided)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return null;
        }

        if (currentProvided && previous is not null &&
            !string.Equals(previous, current, StringComparison.Ordinal))
        {
            return $"{previous} → {current}";
        }

        return current;
    }

    private static void AddCumulativeField(
        List<string> fields,
        string label,
        string? previous,
        string? current,
        bool currentProvided)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        if (currentProvided && previous is not null && !string.Equals(previous, current, StringComparison.Ordinal))
        {
            fields.Add($"{label} {previous} → {current}");
            return;
        }

        fields.Add($"{label}：{current}");
    }

    private static void AddKnownChange(
        List<string> changes,
        string label,
        string? previous,
        string? current)
    {
        if (string.IsNullOrWhiteSpace(current) || string.Equals(previous, current, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(previous is null ? $"{label}：{current}" : $"{label} {previous} → {current}");
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

    private static string GetMagnitudeText(Magnitude magnitude)
    {
        return magnitude.Value is double value ? $"M {value:0.0}" : string.Empty;
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
            EarthquakeReportType.DistantEarthquake => report.DistantEarthquakeKind ==
                DistantEarthquakeKind.VolcanicEruption
                    ? "远地火山喷发"
                    : "远地地震情报",
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

    private static JmaIntensity MaxIntensity(JmaIntensity left, JmaIntensity right)
    {
        return left == JmaIntensity.Unknown ? right :
            right == JmaIntensity.Unknown ? left :
            (JmaIntensity)Math.Max((int)left, (int)right);
    }

    private static string GetIntensityKind(JmaIntensity intensity)
    {
        return intensity.ToString();
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
        OnPropertyChanged(nameof(SummaryOverview));
        OnPropertyChanged(nameof(TsunamiStatus));
        OnPropertyChanged(nameof(SourceDifferences));
        OnPropertyChanged(nameof(HasSourceDifferences));
        OnPropertyChanged(nameof(EventAssociations));
        OnPropertyChanged(nameof(HasEventAssociations));
        OnPropertyChanged(nameof(CanToggleSource));
        OnPropertyChanged(nameof(SourceToggleText));
        OnPropertyChanged(nameof(Observations));
        OnPropertyChanged(nameof(ObservationTreeNodes));
        OnPropertyChanged(nameof(TimelineItems));
        OnPropertyChanged(nameof(RawPayload));
        OnPropertyChanged(nameof(RawMetadataText));
        OnPropertyChanged(nameof(ObservationCountText));
        OnPropertyChanged(nameof(SelectedObservation));
        OnPropertyChanged(nameof(SelectedObservationNode));
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
