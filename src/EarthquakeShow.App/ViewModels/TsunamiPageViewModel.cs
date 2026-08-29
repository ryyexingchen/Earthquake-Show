using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    string InitialText,
    string HighTideText,
    string ObservationStatusText,
    TsunamiLevel Level,
    double? Latitude,
    double? Longitude,
    string PublicationCode,
    string PublicationText,
    bool IsCatalogMatched)
{
    public bool HasMeasuredTsunami => ObservationStatusText is "观测到海啸" or "微弱";
}

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

public enum TsunamiMapDetailLevel
{
    Overview,
    Medium,
    Detailed,
}

public sealed class TsunamiPageViewModel : INotifyPropertyChanged, IDisposable
{
    public const double MinimumMapZoomLevel = 0.5;
    public const double MaximumMapZoomLevel = 16;
    public const double MediumMapZoomThreshold = 2;
    public const double DetailedMapZoomThreshold = 12;

    private readonly ITsunamiReportRepository _repository;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TsunamiMapGeometry _overviewMapGeometry;
    private TsunamiMapGeometry? _mediumMapGeometry;
    private TsunamiMapGeometry? _detailedMapGeometry;
    private readonly JmaTsunamiStationCatalog _fallbackStationCatalog;
    private JmaTsunamiStationCatalog _stationCatalog;
    private TsunamiPageState _state = new();
    private string? _selectedObservationStationCode;
    private string _rawXmlCopyStatus = string.Empty;
    private bool _isDisposed;
    private double _mapZoomLevel = 1;
    private CancellationTokenSource? _mapDetailLoadCancellation;
    private Task? _mapDetailLoadTask;
    private long _mapDetailLoadGeneration;
    private bool _isLoadingMapDetail;

    public TsunamiPageViewModel(
        ITsunamiReportRepository repository,
        JmaTsunamiStationCatalog? stationCatalog = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _fallbackStationCatalog = stationCatalog ?? JmaTsunamiStationCatalog.Empty;
        _stationCatalog = _fallbackStationCatalog;
        _overviewMapGeometry = LoadMapGeometry("jma-tsunami-forecast-lines-low.geojson");
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
            if (_selectedObservationStationCode is not null &&
                !ObservationStations.Any(item => string.Equals(
                    item.Code,
                    _selectedObservationStationCode,
                    StringComparison.Ordinal)))
            {
                _selectedObservationStationCode = null;
            }
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
            OnPropertyChanged(nameof(EarthquakeSourceText));
            OnPropertyChanged(nameof(EarthquakeMagnitudeText));
            OnPropertyChanged(nameof(EarthquakeDepthText));
            OnPropertyChanged(nameof(EarthquakeOriginTimeText));
            OnPropertyChanged(nameof(ObservationSummaryText));
            OnPropertyChanged(nameof(ForecastAreas));
            OnPropertyChanged(nameof(ObservationStations));
            OnPropertyChanged(nameof(SelectedObservationStation));
            OnPropertyChanged(nameof(HasSelectedObservationStation));
            OnPropertyChanged(nameof(EstimationAreas));
            OnPropertyChanged(nameof(HasForecastAreas));
            OnPropertyChanged(nameof(HasObservationStations));
            OnPropertyChanged(nameof(HasEstimationAreas));
            OnPropertyChanged(nameof(ForecastAreaLevels));
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

    public ImmutableArray<TsunamiMapLine> MapLines => CurrentMapGeometry.Lines;

    public MapGeometryBounds MapBounds => _overviewMapGeometry.Bounds;

    public bool HasMapGeometry => !MapLines.IsDefaultOrEmpty;

    public double MapZoomLevel
    {
        get => _mapZoomLevel;
        private set
        {
            value = Math.Clamp(value, MinimumMapZoomLevel, MaximumMapZoomLevel);
            if (Math.Abs(_mapZoomLevel - value) < 0.001)
            {
                return;
            }

            _mapZoomLevel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MapDetailLevel));
            OnPropertyChanged(nameof(MapStatusText));
            OnPropertyChanged(nameof(MapLines));
            RequestMapDetailLoad();
        }
    }

    public TsunamiMapDetailLevel MapDetailLevel =>
        MapZoomLevel > DetailedMapZoomThreshold
            ? TsunamiMapDetailLevel.Detailed
            : MapZoomLevel > MediumMapZoomThreshold
                ? TsunamiMapDetailLevel.Medium
                : TsunamiMapDetailLevel.Overview;

    public bool IsLoadingMapDetail => _isLoadingMapDetail;

    public string MapStatusText =>
        $"{GetMapDetailText(MapDetailLevel)} · {MapZoomLevel:0.0}×" +
        (_isLoadingMapDetail ? " · 加载中" : string.Empty);

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

    public void ZoomMapIn() => MapZoomLevel = Math.Min(MaximumMapZoomLevel, MapZoomLevel + 1);

    public void ZoomMapOut() => MapZoomLevel = Math.Max(MinimumMapZoomLevel, MapZoomLevel - 1);

    public void ResetMapZoom() => MapZoomLevel = 1;

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

    public string EarthquakeSourceText => State.SelectedReport?.Hypocenter?.Name ?? "未提供";

    public string EarthquakeMagnitudeText => State.SelectedReport?.Magnitude?.Value is double value
        ? $"M {value:0.0}"
        : "M 未知";

    public string EarthquakeDepthText => State.SelectedReport?.Hypocenter?.DepthKm is int depth
        ? $"{depth} km"
        : "未提供";

    public string EarthquakeOriginTimeText => FormatTimestamp(State.SelectedReport?.OriginTime);

    public string ObservationSummaryText
    {
        get
        {
            if (State.SelectedReport is null || !HasObservationStations)
            {
                return "当前报文没有沿岸观测记录";
            }

            if (!HasForecastAreas)
            {
                return $"仅收到海啸观测，当前没有预报区发布记录（{ObservationStations.Length} 个观测点）";
            }

            TsunamiLevel observedLevel = ObservationStations
                .Select(item => item.Level)
                .OrderByDescending(GetLevelPriority)
                .FirstOrDefault(TsunamiLevel.Unknown);
            return observedLevel is TsunamiLevel.Unknown
                ? $"已收到 {ObservationStations.Length} 个观测点记录，暂无可量化高度"
                : $"已在 {ObservationStations.Length} 个观测点收到实测记录，最高对应等级：{GetLevelText(observedLevel, null)}";
        }
    }

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
                .Where(item => item.ObservationStatusText != "未观测到海啸")
                .ToImmutableArray();

    public ImmutableArray<TsunamiEstimationAreaDisplay> EstimationAreas =>
        State.SelectedReport is null
            ? []
            : State.SelectedReport.EstimationAreas
                .Select(CreateEstimationAreaDisplay)
                .ToImmutableArray();

    public bool HasForecastAreas => !ForecastAreas.IsDefaultOrEmpty;

    public bool HasObservationStations => !ObservationStations.IsDefaultOrEmpty;

    public TsunamiObservationStationDisplay? SelectedObservationStation
    {
        get => _selectedObservationStationCode is null
            ? null
            : ObservationStations.FirstOrDefault(item => string.Equals(
                item.Code,
                _selectedObservationStationCode,
                StringComparison.Ordinal));
        set
        {
            if (value is null)
            {
                if (_selectedObservationStationCode is null)
                {
                    return;
                }

                _selectedObservationStationCode = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedObservationStation));
                return;
            }

            SelectObservationStation(value.Code);
        }
    }

    public bool HasSelectedObservationStation => SelectedObservationStation is not null;

    public bool TryGetSelectedObservationCoordinate(out GeoCoordinate coordinate)
    {
        TsunamiObservationStationDisplay? station = SelectedObservationStation;
        if (station is null || !station.HasMeasuredTsunami ||
            station.Latitude is not double latitude ||
            station.Longitude is not double longitude ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            coordinate = default;
            return false;
        }

        coordinate = new GeoCoordinate(latitude, longitude);
        return true;
    }

    public bool HasEstimationAreas => !EstimationAreas.IsDefaultOrEmpty;

    public ImmutableDictionary<string, TsunamiLevel> ForecastAreaLevels =>
        State.SelectedReport is null
            ? ImmutableDictionary<string, TsunamiLevel>.Empty
            : State.SelectedReport.ForecastAreas
                .Where(area => !string.IsNullOrWhiteSpace(area.Code))
                .GroupBy(area => area.Code, StringComparer.Ordinal)
                .ToImmutableDictionary(
                    group => group.Key,
                    group => group
                        .Select(area => JmaTsunamiClassifier.Classify(area.KindName, area.KindCode))
                        .OrderByDescending(GetLevelPriority)
                        .FirstOrDefault(TsunamiLevel.Unknown),
                    StringComparer.Ordinal);

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

    private TsunamiMapGeometry CurrentMapGeometry
    {
        get
        {
            return MapDetailLevel switch
            {
                TsunamiMapDetailLevel.Detailed => _detailedMapGeometry ??
                    _mediumMapGeometry ??
                    _overviewMapGeometry,
                TsunamiMapDetailLevel.Medium => _mediumMapGeometry ?? _overviewMapGeometry,
                _ => _overviewMapGeometry,
            };
        }
    }

    private static TsunamiMapGeometry LoadMapGeometry(string fileName)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Data",
            "Map",
            fileName);
        try
        {
            return TsunamiMapGeometry.LoadFromFile(path);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or IOException)
        {
            return TsunamiMapGeometry.Empty;
        }
    }

    private void RequestMapDetailLoad()
    {
        if (_isDisposed || MapDetailLevel == TsunamiMapDetailLevel.Overview ||
            IsMapGeometryLoaded(MapDetailLevel) ||
            _mapDetailLoadTask is { IsCompleted: false })
        {
            if (MapDetailLevel == TsunamiMapDetailLevel.Overview)
            {
                _mapDetailLoadCancellation?.Cancel();
            }

            return;
        }

        _mapDetailLoadCancellation?.Cancel();
        _mapDetailLoadCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _mapDetailLoadCancellation = cancellation;
        long generation = ++_mapDetailLoadGeneration;
        TsunamiMapDetailLevel requestedLevel = MapDetailLevel;
        _isLoadingMapDetail = true;
        OnPropertyChanged(nameof(IsLoadingMapDetail));
        OnPropertyChanged(nameof(MapStatusText));
        _mapDetailLoadTask = LoadMapDetailAsync(requestedLevel, generation, cancellation);
    }

    private async Task LoadMapDetailAsync(
        TsunamiMapDetailLevel requestedLevel,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (RequiresMediumMap(requestedLevel) && _mediumMapGeometry is null)
            {
                TsunamiMapGeometry medium = await Task.Run(
                    () => LoadMapGeometry("jma-tsunami-forecast-lines-medium.geojson"),
                    cancellation.Token);
                if (!IsCurrentMapDetailLoad(generation, cancellation))
                {
                    return;
                }

                _mediumMapGeometry = medium;
                OnPropertyChanged(nameof(MapLines));
            }

            if (requestedLevel == TsunamiMapDetailLevel.Detailed && _detailedMapGeometry is null)
            {
                TsunamiMapGeometry detailed = await Task.Run(
                    () => LoadMapGeometry("jma-tsunami-forecast-lines-overview.geojson"),
                    cancellation.Token);
                if (!IsCurrentMapDetailLoad(generation, cancellation))
                {
                    return;
                }

                _detailedMapGeometry = detailed;
                OnPropertyChanged(nameof(MapLines));
            }
        }
        catch (OperationCanceledException)
        {
            // 缩放回退或视图关闭时，丢弃过期地图精度请求。
        }
        finally
        {
            if (ReferenceEquals(_mapDetailLoadCancellation, cancellation))
            {
                _mapDetailLoadCancellation = null;
                _isLoadingMapDetail = false;
                OnPropertyChanged(nameof(IsLoadingMapDetail));
                OnPropertyChanged(nameof(MapStatusText));
                _mapDetailLoadTask = null;
            }

            cancellation.Dispose();
            if (!_isDisposed && MapDetailLevel != requestedLevel)
            {
                RequestMapDetailLoad();
            }
        }
    }

    private bool IsCurrentMapDetailLoad(
        long generation,
        CancellationTokenSource cancellation) =>
        !_isDisposed &&
        generation == _mapDetailLoadGeneration &&
        ReferenceEquals(_mapDetailLoadCancellation, cancellation) &&
        !cancellation.IsCancellationRequested;

    private bool IsMapGeometryLoaded(TsunamiMapDetailLevel detailLevel) => detailLevel switch
    {
        TsunamiMapDetailLevel.Detailed => _detailedMapGeometry is not null,
        TsunamiMapDetailLevel.Medium => _mediumMapGeometry is not null,
        _ => true,
    };

    private static bool RequiresMediumMap(TsunamiMapDetailLevel detailLevel) =>
        detailLevel is TsunamiMapDetailLevel.Medium or TsunamiMapDetailLevel.Detailed;

    private static string GetMapDetailText(TsunamiMapDetailLevel detailLevel) => detailLevel switch
    {
        TsunamiMapDetailLevel.Detailed => "高精度地图",
        TsunamiMapDetailLevel.Medium => "中精度地图",
        _ => "低精度地图",
    };

    private static TsunamiForecastAreaDisplay CreateForecastAreaDisplay(
        JmaTsunamiForecastArea area)
    {
        TsunamiLevel level = JmaTsunamiClassifier.Classify(area.KindName, area.KindCode);
        if (level is TsunamiLevel.Unknown or TsunamiLevel.Investigating)
        {
            level = ClassifyHeight(area.MaximumHeight);
        }

        return new(
            area.Name,
            area.Code,
            level,
            GetLevelText(level, area.KindName),
            FormatArrival(area.FirstArrivalTime, area.FirstArrivalCondition),
            FormatHeight(area.MaximumHeight));
    }

    private TsunamiObservationStationDisplay CreateObservationStationDisplay(
        JmaTsunamiObservationStation station)
    {
        _stationCatalog.TryGetStation(station.Code, out JmaTsunamiStationCatalogEntry? catalogEntry);
        ImmutableArray<JmaTsunamiPublicationCatalogEntry> publications =
            _stationCatalog.GetPublicationsForStation(station.Code);
        JmaTsunamiPublicationCatalogEntry? publication = publications.FirstOrDefault();
        if (publication is null && _stationCatalog.TryGetPublication(station.Code, out JmaTsunamiPublicationCatalogEntry directPublication))
        {
            publication = directPublication;
        }
        string observationStatus = GetObservationStatus(station);
        TsunamiLevel level = ClassifyHeight(station.MaximumHeight);
        if (level == TsunamiLevel.Unknown && observationStatus == "微弱")
        {
            level = TsunamiLevel.MinorChange;
        }
        return new(
            station.AreaName,
            catalogEntry?.Name ?? station.Name,
            station.Code,
            FormatArrival(station.FirstArrivalTime, station.FirstArrivalCondition),
            FormatHeight(station.MaximumHeight),
            string.IsNullOrWhiteSpace(station.Initial) ? "未提供" : station.Initial!,
            FormatTimestamp(station.MaximumHeightTime),
            observationStatus,
            level,
            catalogEntry?.Latitude,
            catalogEntry?.Longitude,
            publication?.PublicationCode ?? "",
            publication is null ? "" : $"近海发布 {publication.PublicationCode} · {publication.Name}",
            publication is not null);
    }

    private static string GetObservationStatus(JmaTsunamiObservationStation station)
    {
        string text = $"{station.Initial} {station.MaximumHeight?.Description} {station.MaximumHeight?.Condition}";
        if (text.Contains("欠測", StringComparison.Ordinal) || text.Contains("欠测", StringComparison.Ordinal))
        {
            return "欠测";
        }

        if (text.Contains("微弱", StringComparison.Ordinal))
        {
            return "微弱";
        }

        if (station.MaximumHeight?.Meters is double meters && double.IsFinite(meters))
        {
            return meters > 0 ? "观测到海啸" : "未观测到海啸";
        }

        return string.IsNullOrWhiteSpace(station.Initial) ? "未提供" : station.Initial!;
    }

    private static TsunamiLevel ClassifyHeight(JmaTsunamiHeight? height) =>
        height?.Meters is not double meters || !double.IsFinite(meters)
            ? TsunamiLevel.Unknown
            : meters < 0.2
                ? TsunamiLevel.MinorChange
                : meters < 1
                    ? TsunamiLevel.Advisory
                    : meters <= 3
                        ? TsunamiLevel.Warning
                        : TsunamiLevel.MajorWarning;

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
            await LoadStationCatalogAsync(cancellationToken).ConfigureAwait(false);
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
                await LoadStationCatalogAsync(cancellationToken).ConfigureAwait(false);
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

    public bool SelectObservationStation(string stationCode)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(stationCode);
        TsunamiObservationStationDisplay? station = ObservationStations.FirstOrDefault(item =>
            string.Equals(item.Code, stationCode, StringComparison.Ordinal));
        if (station is null)
        {
            return false;
        }

        if (string.Equals(_selectedObservationStationCode, station.Code, StringComparison.Ordinal))
        {
            return true;
        }

        _selectedObservationStationCode = station.Code;
        OnPropertyChanged(nameof(SelectedObservationStation));
        OnPropertyChanged(nameof(HasSelectedObservationStation));
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _mapDetailLoadCancellation?.Cancel();
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

    private async Task LoadStationCatalogAsync(CancellationToken cancellationToken)
    {
        if (_repository is not ITsunamiStationCatalogRepository catalogRepository)
        {
            _stationCatalog = _fallbackStationCatalog;
            return;
        }

        try
        {
            JmaTsunamiStationCatalog catalog =
                await catalogRepository.LoadStationCatalogAsync(cancellationToken).ConfigureAwait(false);
            _stationCatalog = catalog.Stations.IsDefaultOrEmpty && catalog.Publications.IsDefaultOrEmpty
                ? _fallbackStationCatalog
                : catalog;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or System.Data.Common.DbException)
        {
            _stationCatalog = _fallbackStationCatalog;
        }
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
