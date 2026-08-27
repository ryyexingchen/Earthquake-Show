using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public enum EarthquakeMapMarkerKind
{
    Hypocenter,
    Station,
}

public enum EarthquakeMapSelectionKind
{
    Prefecture,
    Area,
    Municipality,
    Station,
}

public sealed record EarthquakeMapSelection(
    EarthquakeMapSelectionKind Kind,
    string Code,
    GeoCoordinate? Coordinate);

public sealed record EarthquakeMapArea(
    string Code,
    string Name,
    JmaIntensity Intensity,
    ImmutableArray<GeoCoordinate> Coordinates)
{
    public string PrefectureCode { get; init; } = string.Empty;

    public ImmutableArray<ImmutableArray<GeoCoordinate>> Rings { get; init; } =
        ImmutableArray.Create(Coordinates);
}

public sealed record EarthquakeMapMunicipality(
    string Code,
    string Name,
    JmaIntensity Intensity,
    ImmutableArray<GeoCoordinate> Coordinates)
{
    public ImmutableArray<ImmutableArray<GeoCoordinate>> Rings { get; init; } =
        ImmutableArray.Create(Coordinates);
}

public sealed record EarthquakeMapMarker(
    EarthquakeMapMarkerKind Kind,
    string Label,
    GeoCoordinate Coordinate,
    JmaIntensity Intensity)
{
    public string Code { get; init; } = string.Empty;
}

public sealed record EarthquakeMapBoundaryLayer(
    JmaIntensity Intensity,
    ImmutableArray<EarthquakeMapBoundary> Boundaries);

public sealed class MapGeometryChangingEventArgs(GeoCoordinate? preferredCenter) : EventArgs
{
    public GeoCoordinate? PreferredCenter { get; } = preferredCenter;
}

public sealed class EarthquakeMapViewModel : INotifyPropertyChanged, IDisposable
{
    // max_small 和 max_big 分别限制手动、自动缩放的最小和最大倍率。
    public const double MaxSmallZoomLevel = 0.5;
    public const double MaxBigZoomLevel = 24;
    public const double MaximumZoomLevel = MaxBigZoomLevel;
    public const double MediumDetailZoomThreshold = 2;
    public const double HighDetailZoomThreshold = 12;

    private readonly EarthquakePageViewModel _page;
    private readonly OfflineMapGeometry _overviewGeometry;
    private readonly OfflineMapGeometry? _overviewMunicipalityGeometry;
    private readonly OfflineMapBoundaryGeometry? _overviewBoundaryGeometry;
    private readonly MapLodResourceProvider? _lodResourceProvider;
    private OfflineMapGeometry _geometry;
    private OfflineMapGeometry? _municipalityGeometry;
    private OfflineMapBoundaryGeometry? _boundaryGeometry;
    private double _zoomLevel = 1;
    private GeoCoordinate? _focusedCoordinate;
    private string? _reportEventId;
    private string? _reportSourceId;
    private string? _reportSourceMessageId;
    private MapDetailLevel _detailLevel;
    private CancellationTokenSource? _detailLoadCancellation;
    private bool _isLoadingDetail;
    private MapDetailLevel? _loadingDetailLevel;
    private MapGeometryBounds? _loadingHighViewportBounds;
    private GeoCoordinate? _loadingViewportCenter;
    private string? _detailLoadError;
    private MapDetailLevel? _detailLoadErrorLevel;
    private MapGeometryBounds? _highLoadedViewportBounds;
    private MapGeometrySet? _mediumGeometrySet;
    private MapGeometrySet? _retainedHighGeometrySet;
    private MapGeometryBounds? _retainedHighViewportBounds;
    private bool _isMapPanning;
    private MapGeometrySet? _pendingGeometrySet;
    private MapDetailLevel? _pendingGeometryLevel;
    private MapGeometryBounds? _pendingGeometryBounds;
    private GeoCoordinate? _pendingGeometryCenter;
    private long _detailLoadGeneration;
    private bool _lastHighLoadUsedCache;
    private bool _isApplyingAutoScale;
    private bool _isDisposed;
    private EarthquakeMapSelection? _selectedMapSelection;
    private EarthquakeMapViewState _lastMapState;
    private string? _selectedEventId;

    public EarthquakeMapViewModel(
        EarthquakePageViewModel page,
        OfflineMapGeometry geometry,
        OfflineMapGeometry? municipalityGeometry = null,
        OfflineMapBoundaryGeometry? boundaryGeometry = null,
        MapLodResourceProvider? lodResourceProvider = null)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _overviewGeometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _overviewMunicipalityGeometry = municipalityGeometry;
        _overviewBoundaryGeometry = boundaryGeometry;
        _geometry = _overviewGeometry;
        _municipalityGeometry = _overviewMunicipalityGeometry;
        _boundaryGeometry = _overviewBoundaryGeometry;
        _lodResourceProvider = lodResourceProvider;
        _lastMapState = _page.State.Map;
        _selectedEventId = _page.State.SelectedEvent?.EventId;
        _page.PropertyChanged += OnPagePropertyChanged;
        RebuildLayers();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<MapGeometryChangingEventArgs>? GeometryChanging;

    public ImmutableArray<MapPolygonGeometry> Outline => _geometry.Polygons;

    public string GeometrySource => _geometry.Source;

    public bool IsOfficialBoundary => _geometry.IsOfficialBoundary;

    public MapGeometryBounds GeometryBounds => _geometry.Bounds;

    public OfflineMapBoundaryGeometry? BoundaryGeometry => _boundaryGeometry;

    public int InvalidGeometryCount => _geometry.InvalidGeometryCount;

    public MapDetailLevel DetailLevel => _detailLevel;

    internal MapGeometryBounds? HighLoadedViewportBounds =>
        _highLoadedViewportBounds;

    public bool IsLoadingHighDetail =>
        _isLoadingDetail && _loadingDetailLevel == MapDetailLevel.High;

    public bool LastHighLoadUsedCache => _lastHighLoadUsedCache;

    public bool IsLoadingDetail
    {
        get => _isLoadingDetail;
        private set
        {
            if (_isLoadingDetail == value)
            {
                return;
            }

            _isLoadingDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoadingHighDetail));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string? DetailLoadError => _detailLoadError;

    public int UnmappedAreaCount { get; private set; }

    public int UnmappedMunicipalityCount { get; private set; }

    public IReadOnlyList<EarthquakeMapArea> Areas { get; private set; } = [];

    public IReadOnlyList<EarthquakeMapMunicipality> Municipalities { get; private set; } = [];

    public IReadOnlyList<EarthquakeMapBoundaryLayer> BoundaryLayers { get; private set; } = [];

    public IReadOnlyList<EarthquakeMapMarker> Markers { get; private set; } = [];

    public EarthquakeMapSelection? SelectedMapSelection => _selectedMapSelection;

    public IReadOnlyList<EarthquakeMapArea> SelectedAreaHighlights =>
        _selectedMapSelection?.Kind switch
        {
            EarthquakeMapSelectionKind.Prefecture => GetSelectedAreaHighlights(
                _selectedMapSelection),
            EarthquakeMapSelectionKind.Area => GetSelectedAreaHighlights(
                _selectedMapSelection),
            _ => [],
        };

    public IReadOnlyList<EarthquakeMapMunicipality> SelectedMunicipalityHighlights =>
        _selectedMapSelection?.Kind == EarthquakeMapSelectionKind.Municipality
            ? GetSelectedMunicipalityHighlights(_selectedMapSelection)
            : [];

    public EarthquakeMapMarker? SelectedStationHighlight =>
        _selectedMapSelection?.Kind == EarthquakeMapSelectionKind.Station
            ? Markers.FirstOrDefault(marker =>
                marker.Kind == EarthquakeMapMarkerKind.Station &&
                string.Equals(
                    marker.Code,
                    _selectedMapSelection.Code,
                    StringComparison.Ordinal))
            : null;

    private IReadOnlyList<EarthquakeMapArea> GetSelectedAreaHighlights(
        EarthquakeMapSelection selection)
    {
        EarthquakeMapArea[] current = Areas
            .Where(area => selection.Kind == EarthquakeMapSelectionKind.Prefecture
                ? string.Equals(
                    area.PrefectureCode,
                    selection.Code,
                    StringComparison.Ordinal)
                : string.Equals(area.Code, selection.Code, StringComparison.Ordinal))
            .ToArray();
        if (current.Length > 0)
        {
            return current;
        }

        EarthquakeReport? viewedReport = _page.State.ViewedReport;
        if (viewedReport is null)
        {
            return [];
        }

        EarthquakeReport report = GetMapReport(viewedReport);
        Dictionary<string, IntensityArea> reportAreas = report.IntensityAreas
            .GroupBy(area => area.Code, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(area => area.MaxIntensity).First(),
                StringComparer.Ordinal);
        HashSet<string> codes = selection.Kind == EarthquakeMapSelectionKind.Prefecture
            ? report.IntensityAreas
                .Where(area => string.Equals(
                    area.PrefectureCode,
                    selection.Code,
                    StringComparison.Ordinal))
                .Select(area => area.Code)
                .ToHashSet(StringComparer.Ordinal)
            : [selection.Code];

        return BuildAreaHighlights(
            _overviewGeometry.Polygons.Where(polygon => codes.Contains(polygon.Code)),
            reportAreas);
    }

    private IReadOnlyList<EarthquakeMapMunicipality> GetSelectedMunicipalityHighlights(
        EarthquakeMapSelection selection)
    {
        EarthquakeMapMunicipality[] current = Municipalities
            .Where(item => string.Equals(item.Code, selection.Code, StringComparison.Ordinal))
            .ToArray();
        if (current.Length > 0)
        {
            return current;
        }

        EarthquakeReport? viewedReport = _page.State.ViewedReport;
        if (viewedReport is null)
        {
            return [];
        }

        EarthquakeReport report = GetMapReport(viewedReport);
        Dictionary<string, IntensityMunicipality> reportMunicipalities = report.IntensityMunicipalities
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.MaxIntensity).First(),
                StringComparer.Ordinal);
        return BuildMunicipalityHighlights(
            FindMunicipalityGeometry(selection.Code),
            reportMunicipalities);
    }

    private static IReadOnlyList<EarthquakeMapArea> BuildAreaHighlights(
        IEnumerable<MapPolygonGeometry> polygons,
        IReadOnlyDictionary<string, IntensityArea> reportAreas)
    {
        return polygons
            .GroupBy(polygon => polygon.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                MapPolygonGeometry first = group.First();
                reportAreas.TryGetValue(group.Key, out IntensityArea? source);
                ImmutableArray<ImmutableArray<GeoCoordinate>> rings = group
                    .SelectMany(GetPolygonRings)
                    .ToImmutableArray();
                ImmutableArray<GeoCoordinate> coordinates = rings.IsDefaultOrEmpty
                    ? first.Coordinates
                    : rings[0];
                return new EarthquakeMapArea(
                    group.Key,
                    source?.Name ?? first.Name,
                    source?.MaxIntensity ?? JmaIntensity.Unknown,
                    coordinates)
                {
                    PrefectureCode = source?.PrefectureCode ?? string.Empty,
                    Rings = rings,
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<EarthquakeMapMunicipality> BuildMunicipalityHighlights(
        IEnumerable<MapPolygonGeometry> polygons,
        IReadOnlyDictionary<string, IntensityMunicipality> reportMunicipalities)
    {
        return polygons
            .GroupBy(polygon => polygon.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                MapPolygonGeometry first = group.First();
                reportMunicipalities.TryGetValue(group.Key, out IntensityMunicipality? source);
                ImmutableArray<ImmutableArray<GeoCoordinate>> rings = group
                    .SelectMany(GetPolygonRings)
                    .ToImmutableArray();
                ImmutableArray<GeoCoordinate> coordinates = rings.IsDefaultOrEmpty
                    ? first.Coordinates
                    : rings[0];
                return new EarthquakeMapMunicipality(
                    group.Key,
                    source?.Name ?? first.Name,
                    source?.MaxIntensity ?? JmaIntensity.Unknown,
                    coordinates)
                {
                    Rings = rings,
                };
            })
            .ToArray();
    }

    private IEnumerable<MapPolygonGeometry> FindAreaGeometry(string areaCode)
    {
        MapPolygonGeometry[] current = Outline
            .Where(item => string.Equals(item.Code, areaCode, StringComparison.Ordinal))
            .ToArray();
        return current.Length > 0
            ? current
            : _overviewGeometry.Polygons.Where(item =>
                string.Equals(item.Code, areaCode, StringComparison.Ordinal));
    }

    private IEnumerable<MapPolygonGeometry> FindMunicipalityGeometry(string municipalityCode)
    {
        MapPolygonGeometry[] current = (_municipalityGeometry?.Polygons ?? [])
            .Where(item => string.Equals(item.Code, municipalityCode, StringComparison.Ordinal))
            .ToArray();
        if (current.Length > 0)
        {
            return current;
        }

        return (_overviewMunicipalityGeometry?.Polygons ?? [])
            .Where(item => string.Equals(item.Code, municipalityCode, StringComparison.Ordinal));
    }

    private static IEnumerable<ImmutableArray<GeoCoordinate>> GetPolygonRings(
        MapPolygonGeometry polygon)
    {
        return polygon.Rings.IsDefaultOrEmpty
            ? [polygon.Coordinates]
            : polygon.Rings;
    }

    public GeoCoordinate? FocusedCoordinate => _focusedCoordinate;

    public void SelectObservation(
        string? kind,
        string? code,
        GeoCoordinate? coordinate)
    {
        EarthquakeMapSelection? selection = kind switch
        {
            "都道府县" when !string.IsNullOrWhiteSpace(code) =>
                new(EarthquakeMapSelectionKind.Prefecture, code, coordinate),
            "区域" when !string.IsNullOrWhiteSpace(code) =>
                new(EarthquakeMapSelectionKind.Area, code, coordinate),
            "市町村" when !string.IsNullOrWhiteSpace(code) =>
                new(EarthquakeMapSelectionKind.Municipality, code, coordinate),
            "观测点" when !string.IsNullOrWhiteSpace(code) =>
                new(EarthquakeMapSelectionKind.Station, code, coordinate),
            _ => null,
        };

        if (_selectedMapSelection == selection)
        {
            return;
        }

        _selectedMapSelection = selection;
        OnPropertyChanged(nameof(SelectedMapSelection));
        OnPropertyChanged(nameof(SelectedAreaHighlights));
        OnPropertyChanged(nameof(SelectedMunicipalityHighlights));
        OnPropertyChanged(nameof(SelectedStationHighlight));
    }

    public void ClearSelectedObservation()
    {
        SelectObservation(null, null, null);
    }

    public bool TryGetSelectedObservationView(
        out GeoCoordinate center,
        out MapGeometryBounds bounds)
    {
        IEnumerable<GeoCoordinate> points = _selectedMapSelection?.Kind switch
        {
            EarthquakeMapSelectionKind.Station when SelectedStationHighlight is EarthquakeMapMarker marker =>
                [marker.Coordinate],
            EarthquakeMapSelectionKind.Municipality => SelectedMunicipalityHighlights
                .SelectMany(item => item.Rings)
                .SelectMany(ring => ring),
            EarthquakeMapSelectionKind.Prefecture or EarthquakeMapSelectionKind.Area =>
                SelectedAreaHighlights
                    .SelectMany(item => item.Rings)
                    .SelectMany(ring => ring),
            _ => [],
        };
        GeoCoordinate[] materialized = points.ToArray();
        if (materialized.Length == 0 && _selectedMapSelection?.Coordinate is GeoCoordinate coordinate)
        {
            materialized = [coordinate];
        }

        if (!TryGetBoundsCenter(materialized, out center))
        {
            bounds = default;
            return false;
        }

        bounds = new MapGeometryBounds(
            materialized.Min(item => item.Longitude),
            materialized.Max(item => item.Longitude),
            materialized.Min(item => item.Latitude),
            materialized.Max(item => item.Latitude));
        return true;
    }

    public void FocusSelectedObservation()
    {
        ThrowIfDisposed();
        if (TryGetSelectedObservationView(
                out GeoCoordinate center,
                out _))
        {
            FocusLocation(center);
        }
    }

    public void AutoScalePreservingFocus(double automaticZoomLevel)
    {
        ThrowIfDisposed();
        ZoomLevel = Math.Clamp(
            automaticZoomLevel,
            MaxSmallZoomLevel,
            MaxBigZoomLevel);
    }

    internal MapGeometryBounds OverviewBounds => _overviewGeometry.Bounds;

    public string? ViewedReportKey
    {
        get
        {
            EarthquakeReport? report = _page.State.ViewedReport;
            return report is null
                ? null
                : $"{report.EventId}\u001f{report.Source.SourceId}\u001f{report.Source.SourceMessageId}";
        }
    }

    public EarthquakeReportType ViewedReportType => GetViewedReportType();

    public double ZoomLevel
    {
        get => _zoomLevel;
        private set
        {
            value = Math.Clamp(value, MaxSmallZoomLevel, MaxBigZoomLevel);
            if (Math.Abs(_zoomLevel - value) < 0.001)
            {
                return;
            }

            _zoomLevel = value;
            OnPropertyChanged();
        }
    }

    public EarthquakeMapFocusMode FocusMode => _page.State.Map.FocusMode;

    public bool FollowSelection => _page.State.Map.FollowSelection;

    public EarthquakeMapFocusMode EffectiveFocusMode =>
        FollowSelection && HasSelectedEvent
            ? EarthquakeMapFocusMode.SelectedEvent
            : FocusMode;

    public bool HasSelectedEvent => _page.State.SelectedEvent is not null;

    public bool IsDistantEvent =>
        _page.State.ViewedReport?.ReportType == EarthquakeReportType.DistantEarthquake;

    public bool HasDrawableLayers =>
        Municipalities.Count > 0 || BoundaryLayers.Count > 0 || Markers.Count > 0;

    public bool TryGetAreaFocusCoordinate(
        string areaCode,
        out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (string.IsNullOrWhiteSpace(areaCode))
        {
            return false;
        }

        IReadOnlyList<GeoCoordinate> points = FindAreaGeometry(areaCode)
            .SelectMany(GetPolygonRings)
            .SelectMany(ring => ring)
            .ToArray();
        return TryGetBoundsCenter(points, out coordinate);
    }

    public bool TryGetMunicipalityFocusCoordinate(
        string municipalityCode,
        out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (string.IsNullOrWhiteSpace(municipalityCode))
        {
            return false;
        }

        IReadOnlyList<GeoCoordinate> points = FindMunicipalityGeometry(municipalityCode)
            .SelectMany(GetPolygonRings)
            .SelectMany(ring => ring)
            .ToArray();
        return TryGetBoundsCenter(points, out coordinate);
    }

    public bool TryGetSelectedEventFocusCoordinate(out GeoCoordinate coordinate)
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        if (report?.Hypocenter?.Coordinate is GeoCoordinate hypocenter)
        {
            coordinate = hypocenter;
            return true;
        }

        IReadOnlyList<GeoCoordinate> points = Markers
            .Select(marker => marker.Coordinate)
            .Concat(Areas.SelectMany(area => area.Rings.SelectMany(ring => ring)))
            .Concat(Municipalities.SelectMany(item => item.Rings.SelectMany(ring => ring)))
            .ToArray();
        return TryGetBoundsCenter(points, out coordinate);
    }

    public bool TryGetSelectedEventBounds(out MapGeometryBounds bounds)
    {
        IReadOnlyList<GeoCoordinate> points = Markers
            .Select(marker => marker.Coordinate)
            .Concat(Areas.SelectMany(area => area.Rings.SelectMany(ring => ring)))
            .Concat(Municipalities.SelectMany(item => item.Rings.SelectMany(ring => ring)))
            .ToArray();
        if (points.Count == 0)
        {
            bounds = default;
            return false;
        }

        bounds = new MapGeometryBounds(
            points.Min(item => item.Longitude),
            points.Max(item => item.Longitude),
            points.Min(item => item.Latitude),
            points.Max(item => item.Latitude));
        return true;
    }

    public string StatusText
    {
        get
        {
            string baseText;
            if (!IsOfficialBoundary)
            {
                baseText = HasSelectedEvent
                    ? "离线示意底图 · 当前事件图层"
                    : "离线示意底图 · 未选择事件";
            }
            else
            {
                baseText = HasSelectedEvent ? "离线地图 · 当前事件图层" : "离线地图 · 未选择事件";
            }

            if (IsLoadingDetail)
            {
                return IsLoadingHighDetail
                    ? DetailLevel == MapDetailLevel.Medium
                        ? $"{baseText} · 中精度兜底，正在加载高精度"
                        : $"{baseText} · 正在加载高精度"
                    : $"{baseText} · 正在加载中精度";
            }

            if (!string.IsNullOrWhiteSpace(DetailLoadError))
            {
                string levelText = _detailLoadErrorLevel == MapDetailLevel.High
                    ? "高精度"
                    : "中精度";
                return $"{baseText} · {levelText}加载失败";
            }

            return DetailLevel switch
            {
                MapDetailLevel.High => LastHighLoadUsedCache
                    ? $"{baseText} · 高精度 · 缓存"
                    : $"{baseText} · 高精度",
                MapDetailLevel.Medium => $"{baseText} · 中精度",
                _ => baseText,
            };
        }
    }

    public async Task EnsureDetailLevelForZoomAsync(
        CancellationToken cancellationToken = default,
        MapGeometryBounds? viewportBounds = null,
        GeoCoordinate? viewportCenter = null)
    {
        ThrowIfDisposed();
        MapDetailLevel desiredLevel = GetDesiredDetailLevel();
        TraceDetail(
            "EnsureStart",
            $"generation={_detailLoadGeneration} desired={desiredLevel} current={_detailLevel} " +
            $"viewport={FormatBounds(viewportBounds)} center={FormatCoordinate(viewportCenter)}");
        if (desiredLevel == MapDetailLevel.Overview)
        {
            ClearPendingGeometry();
            _detailLoadCancellation?.Cancel();
            _detailLoadGeneration++;
            _loadingHighViewportBounds = null;
            _loadingViewportCenter = null;
            if (_detailLevel != MapDetailLevel.Overview)
            {
                ApplyGeometrySet(
                    new MapGeometrySet(
                        _overviewGeometry,
                        _overviewMunicipalityGeometry,
                        _overviewBoundaryGeometry),
                    MapDetailLevel.Overview,
                    preferredCenter: viewportCenter);
            }

            _mediumGeometrySet = null;
            _retainedHighGeometrySet = null;
            _retainedHighViewportBounds = null;

            return;
        }

        if (!_isMapPanning && _pendingGeometrySet is not null &&
            _pendingGeometryLevel == desiredLevel &&
            (desiredLevel != MapDetailLevel.High ||
                (_pendingGeometryBounds is MapGeometryBounds pendingBounds &&
                    viewportBounds is MapGeometryBounds requestedBounds &&
                    Contains(
                        pendingBounds,
                        NormalizeHighViewportBounds(requestedBounds)!.Value))))
        {
            MapGeometrySet pendingGeometrySet = _pendingGeometrySet;
            MapGeometryBounds? deferredBounds = _pendingGeometryBounds;
            GeoCoordinate? pendingCenter = _pendingGeometryCenter;
            ClearPendingGeometry();
            ApplyGeometrySet(pendingGeometrySet, desiredLevel, deferredBounds, viewportCenter ?? pendingCenter);
            TraceDetail(
                "ApplyDeferred",
                $"detail={desiredLevel} viewport={FormatBounds(viewportBounds)} " +
                $"center={FormatCoordinate(viewportCenter ?? pendingCenter)}");
            return;
        }

        if (_pendingGeometrySet is not null)
        {
            ClearPendingGeometry();
        }

        MapGeometryBounds? normalizedHighViewportBounds = desiredLevel == MapDetailLevel.High
            ? NormalizeHighViewportBounds(viewportBounds)
            : viewportBounds;

        if (_isLoadingDetail && _loadingDetailLevel != desiredLevel)
        {
            TraceDetail(
                "CancelInFlightLevelChange",
                $"from={_loadingDetailLevel} to={desiredLevel}");
            _detailLoadCancellation?.Cancel();
            _detailLoadGeneration++;
        }

        if (ShouldReuseInFlightDetailLoad(
                _isLoadingDetail,
                _loadingDetailLevel,
                desiredLevel))
        {
            if (viewportCenter is not null)
            {
                _loadingViewportCenter = viewportCenter;
            }

            TraceDetail(
                "EnsureReuseInFlight",
                $"detail={desiredLevel} viewport={FormatBounds(viewportBounds)} " +
                $"center={FormatCoordinate(_loadingViewportCenter)}");
            return;
        }

        // 连续缩放/拖动时复用仍在进行的高精度加载，避免重复解析大型 GeoJSON。
        // 视口中心单独更新，确保异步加载完成后使用用户最后看到的位置。
        if (ShouldReuseInFlightHighLoad(
                _isLoadingDetail,
                _loadingDetailLevel,
                _loadingHighViewportBounds,
                desiredLevel,
                normalizedHighViewportBounds))
        {
            if (viewportCenter is not null)
            {
                _loadingViewportCenter = viewportCenter;
            }

            TraceDetail(
                "EnsureReuseInFlight",
                $"detail={desiredLevel} viewport={FormatBounds(viewportBounds)} " +
                $"loading={FormatBounds(_loadingHighViewportBounds)} " +
                $"center={FormatCoordinate(_loadingViewportCenter)}");
            return;
        }

        if (_detailLevel == desiredLevel)
        {
            if (!NeedsHighDetailReload(
                    desiredLevel,
                    _highLoadedViewportBounds,
                    normalizedHighViewportBounds))
            {
                TraceDetail("EnsureReuseCurrent", $"detail={_detailLevel} viewport={FormatBounds(viewportBounds)}");
                return;
            }
        }

        if (desiredLevel == MapDetailLevel.High &&
            normalizedHighViewportBounds is MapGeometryBounds highBounds &&
            _retainedHighGeometrySet is not null &&
            _retainedHighViewportBounds is MapGeometryBounds retainedBounds &&
            Contains(retainedBounds, highBounds))
        {
            TraceDetail("EnsureReuseRetained", $"viewport={FormatBounds(highBounds)} retained={FormatBounds(retainedBounds)}");
            _lastHighLoadUsedCache = true;
            ApplyGeometrySet(
                _retainedHighGeometrySet,
                MapDetailLevel.High,
                retainedBounds,
                viewportCenter);
            _retainedHighGeometrySet = null;
            _retainedHighViewportBounds = null;
            return;
        }

        if (_lodResourceProvider is null)
        {
            return;
        }

        _detailLoadCancellation?.Cancel();
        _detailLoadCancellation?.Dispose();
        long loadGeneration = ++_detailLoadGeneration;
        TraceDetail("LoadBegin", $"generation={loadGeneration} desired={desiredLevel}");
        CancellationTokenSource loadCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _detailLoadCancellation = loadCancellation;
        _detailLoadErrorLevel = desiredLevel;
        _loadingDetailLevel = desiredLevel;
        _loadingHighViewportBounds = desiredLevel == MapDetailLevel.High
            ? normalizedHighViewportBounds
            : null;
        _loadingViewportCenter = viewportCenter;
        _lastHighLoadUsedCache = false;
        IsLoadingDetail = true;
        long loadStarted = Stopwatch.GetTimestamp();
        try
        {
            if (desiredLevel == MapDetailLevel.High &&
                _mediumGeometrySet is null)
            {
                MapGeometrySet? mediumGeometrySet = await Task.Run(
                    () => _lodResourceProvider.TryLoadMedium(loadCancellation.Token),
                    CancellationToken.None);
                if (mediumGeometrySet is null ||
                    loadCancellation.IsCancellationRequested ||
                    loadGeneration != _detailLoadGeneration ||
                    _isDisposed)
                {
                    return;
                }

                _mediumGeometrySet = mediumGeometrySet;
                TraceDetail(
                    "MediumLoaded",
                    $"generation={loadGeneration} elapsed={Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds:0.##}ms");
            }

            if (desiredLevel == MapDetailLevel.High &&
                _detailLevel == MapDetailLevel.Overview)
            {
                await Task.Yield();
                if (loadGeneration != _detailLoadGeneration ||
                    loadCancellation.IsCancellationRequested ||
                    _isDisposed)
                {
                    return;
                }

                if (!_isMapPanning)
                {
                    ApplyMediumFallback(_loadingViewportCenter);
                }
                else
                {
                    TraceDetail("MediumFallbackDeferred", $"generation={loadGeneration}");
                }
                await Task.Yield();
                if (loadGeneration != _detailLoadGeneration ||
                    loadCancellation.IsCancellationRequested ||
                    _isDisposed)
                {
                    return;
                }
            }

            MapGeometrySet? geometrySet = await Task.Run(
                () => desiredLevel == MapDetailLevel.High
                    ? _lodResourceProvider.TryLoadHigh(
                        loadCancellation.Token,
                        normalizedHighViewportBounds)
                    : _lodResourceProvider.TryLoadMedium(loadCancellation.Token),
                CancellationToken.None);
            if (geometrySet is null ||
                loadCancellation.IsCancellationRequested ||
                _isDisposed ||
                loadGeneration != _detailLoadGeneration)
            {
                return;
            }

            if (desiredLevel == MapDetailLevel.High &&
                _lodResourceProvider.LastHighLoadUsedCache)
            {
                TraceDetail(
                    "HighResourceCacheHit",
                    $"generation={loadGeneration} viewport={FormatBounds(viewportBounds)}");
            }

            if (_isMapPanning)
            {
                _pendingGeometrySet = geometrySet;
                _pendingGeometryLevel = desiredLevel;
                _pendingGeometryBounds = normalizedHighViewportBounds;
                _pendingGeometryCenter = _loadingViewportCenter;
                TraceDetail(
                    "LoadReadyDeferred",
                    $"generation={loadGeneration} detail={desiredLevel} " +
                    $"elapsed={Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds:0.##}ms " +
                    $"viewport={FormatBounds(viewportBounds)} center={FormatCoordinate(_loadingViewportCenter)}");
                return;
            }

            ApplyGeometrySet(
                geometrySet,
                desiredLevel,
                normalizedHighViewportBounds,
                _loadingViewportCenter);
            TraceDetail(
                "LoadApplied",
                $"generation={loadGeneration} detail={desiredLevel} " +
                $"elapsed={Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds:0.##}ms " +
                $"center={FormatCoordinate(_loadingViewportCenter)}");
        }
        catch (OperationCanceledException)
        {
            // 缩放方向改变时取消旧级别加载，保留当前可见地图。
            TraceDetail("LoadCancelled", $"generation={loadGeneration}");
        }
        catch (Exception exception)
        {
            TraceDetail("LoadFailed", $"generation={loadGeneration} error={exception.Message}");
            _detailLoadError = exception.Message;
            _detailLoadErrorLevel = desiredLevel;
            OnPropertyChanged(nameof(DetailLoadError));
            OnPropertyChanged(nameof(StatusText));
        }
        finally
        {
            if (ReferenceEquals(_detailLoadCancellation, loadCancellation))
            {
                _detailLoadCancellation = null;
                _loadingDetailLevel = null;
                _loadingHighViewportBounds = null;
                _loadingViewportCenter = null;
                IsLoadingDetail = false;
            }

            loadCancellation.Dispose();
        }
    }

    internal bool WillChangeDetailLevel(
        MapGeometryBounds? viewportBounds)
    {
        MapDetailLevel desiredLevel = GetDesiredDetailLevel();
        return desiredLevel != DetailLevel ||
            NeedsHighDetailReload(
                desiredLevel,
                _highLoadedViewportBounds,
                viewportBounds);
    }

    internal bool WillSwitchDetailLevel()
    {
        return GetDesiredDetailLevel() != DetailLevel;
    }

    internal void SetMapPanning(bool isPanning)
    {
        _isMapPanning = isPanning;
    }

    private void ClearPendingGeometry()
    {
        _pendingGeometrySet = null;
        _pendingGeometryLevel = null;
        _pendingGeometryBounds = null;
        _pendingGeometryCenter = null;
    }

    public void ZoomIn()
    {
        ThrowIfDisposed();
        ZoomLevel = Math.Min(MaxBigZoomLevel, ZoomLevel + 1);
    }

    public void ZoomOut()
    {
        ThrowIfDisposed();
        ZoomLevel = Math.Max(MaxSmallZoomLevel, ZoomLevel - 1);
    }

    public void AutoScale(double automaticZoomLevel = 1)
    {
        ThrowIfDisposed();
        TraceDetail(
            "AutoScale",
            $"requested={automaticZoomLevel:0.###} current={ZoomLevel:0.###} " +
            $"manualPan={_isMapPanning} event={_page.State.ViewedReport?.EventId ?? "null"}");
        ZoomLevel = Math.Clamp(
            automaticZoomLevel,
            MaxSmallZoomLevel,
            MaxBigZoomLevel);
        if (_focusedCoordinate is not null)
        {
            _focusedCoordinate = null;
            OnPropertyChanged(nameof(FocusedCoordinate));
        }

        if (HasSelectedEvent &&
            (FocusMode != EarthquakeMapFocusMode.SelectedEvent || !FollowSelection))
        {
            _page.SetMapViewState(_page.State.Map with
            {
                FocusMode = EarthquakeMapFocusMode.SelectedEvent,
                FollowSelection = true,
            });
        }
    }

    public void ResetView()
    {
        ThrowIfDisposed();
        ZoomLevel = 1;
        _focusedCoordinate = null;
        _page.SetMapViewState(_page.State.Map with
        {
            FocusMode = EarthquakeMapFocusMode.JapanOverview,
            FollowSelection = false,
        });
    }

    public void FocusSelectedEvent()
    {
        ThrowIfDisposed();
        if (!HasSelectedEvent)
        {
            return;
        }

        _focusedCoordinate = null;
        _page.SetMapViewState(_page.State.Map with
        {
            FocusMode = EarthquakeMapFocusMode.SelectedEvent,
            FollowSelection = true,
        });
        OnPropertyChanged(nameof(FollowSelection));
        OnPropertyChanged(nameof(EffectiveFocusMode));
    }

    public void FocusLocation(GeoCoordinate coordinate)
    {
        ThrowIfDisposed();
        _focusedCoordinate = coordinate;
        ZoomLevel = Math.Max(2, ZoomLevel);
        OnPropertyChanged(nameof(FocusedCoordinate));
    }

    public void SetFollowSelection(bool followSelection)
    {
        ThrowIfDisposed();
        GeoCoordinate? previousFocusedCoordinate = _focusedCoordinate;
        if (followSelection)
        {
            _focusedCoordinate = null;
        }
        else if (FollowSelection && TryGetSelectedEventFocusCoordinate(
            out GeoCoordinate selectedEventFocus))
        {
            _focusedCoordinate = selectedEventFocus;
        }

        _page.SetMapViewState(_page.State.Map with
        {
            FocusMode = !followSelection &&
                previousFocusedCoordinate is null &&
                HasSelectedEvent
                ? EarthquakeMapFocusMode.SelectedEvent
                : _page.State.Map.FocusMode,
            FollowSelection = followSelection,
        });
        if (previousFocusedCoordinate != _focusedCoordinate)
        {
            OnPropertyChanged(nameof(FocusedCoordinate));
        }
    }

    public void BeginManualInteraction()
    {
        SetFollowSelection(false);
    }

    internal static MapGeometryBounds CenterEventBounds(
        MapGeometryBounds bounds,
        GeoCoordinate center)
    {
        const double minimumSpan = 0.25;
        double longitudeHalfSpan = Math.Max(
            minimumSpan / 2,
            Math.Max(
                center.Longitude - bounds.MinLongitude,
                bounds.MaxLongitude - center.Longitude));
        double latitudeHalfSpan = Math.Max(
            minimumSpan / 2,
            Math.Max(
                center.Latitude - bounds.MinLatitude,
                bounds.MaxLatitude - center.Latitude));
        return new MapGeometryBounds(
            center.Longitude - longitudeHalfSpan,
            center.Longitude + longitudeHalfSpan,
            center.Latitude - latitudeHalfSpan,
            center.Latitude + latitudeHalfSpan);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _page.PropertyChanged -= OnPagePropertyChanged;
        _detailLoadCancellation?.Cancel();
        _detailLoadCancellation?.Dispose();
        _detailLoadCancellation = null;
        ClearPendingGeometry();
        _isDisposed = true;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(EarthquakePageViewModel.State))
        {
            EarthquakePageState state = _page.State;
            bool reportChanged = IsViewedReportChanged(state.ViewedReport);
            bool mapStateChanged = _lastMapState != state.Map;
            bool selectedEventChanged = !string.Equals(
                _selectedEventId,
                state.SelectedEvent?.EventId,
                StringComparison.Ordinal);
            _lastMapState = state.Map;
            _selectedEventId = state.SelectedEvent?.EventId;
            if (ShouldAutoScaleAfterReportChange(
                    _isApplyingAutoScale,
                    _isMapPanning,
                    reportChanged))
            {
                _isApplyingAutoScale = true;
                try
                {
                    AutoScale();
                }
                finally
                {
                    _isApplyingAutoScale = false;
                }
            }
            else if (reportChanged && _isMapPanning)
            {
                TraceDetail("AutoScaleSkipped", "reason=manual-pan");
            }

            if (reportChanged || selectedEventChanged)
            {
                RebuildLayers();
            }
            else if (mapStateChanged)
            {
                RaiseMapStateProperties();
            }
        }
    }

    private void RaiseMapStateProperties()
    {
        OnPropertyChanged(nameof(FocusMode));
        OnPropertyChanged(nameof(FollowSelection));
        OnPropertyChanged(nameof(EffectiveFocusMode));
    }

    internal static bool ShouldAutoScaleAfterReportChange(
        bool isApplyingAutoScale,
        bool isMapPanning,
        bool reportChanged)
    {
        return !isApplyingAutoScale && !isMapPanning && reportChanged;
    }

    private bool IsViewedReportChanged(EarthquakeReport? report)
    {
        return !string.Equals(_reportEventId, report?.EventId, StringComparison.Ordinal) ||
            !string.Equals(_reportSourceId, report?.Source.SourceId, StringComparison.Ordinal) ||
            !string.Equals(
                _reportSourceMessageId,
                report?.Source.SourceMessageId,
                StringComparison.Ordinal);
    }

    private void ApplyGeometrySet(
        MapGeometrySet geometrySet,
        MapDetailLevel detailLevel,
        MapGeometryBounds? highViewportBounds = null,
        GeoCoordinate? preferredCenter = null)
    {
        if (_detailLevel == MapDetailLevel.High &&
            _highLoadedViewportBounds is MapGeometryBounds previousHighBounds)
        {
            _retainedHighGeometrySet = new MapGeometrySet(
                _geometry,
                _municipalityGeometry,
                _boundaryGeometry);
            _retainedHighViewportBounds = previousHighBounds;
        }

        GeometryChanging?.Invoke(
            this,
            new MapGeometryChangingEventArgs(preferredCenter));
        TraceDetail(
            "ApplyGeometry",
            $"from={_detailLevel} to={detailLevel} center={FormatCoordinate(preferredCenter)} " +
            $"viewport={FormatBounds(highViewportBounds)}");
        _geometry = geometrySet.Areas;
        _municipalityGeometry = geometrySet.Municipalities;
        _boundaryGeometry = geometrySet.Boundaries;
        _detailLevel = detailLevel;
        _detailLoadError = null;
        _detailLoadErrorLevel = null;
        _highLoadedViewportBounds = detailLevel == MapDetailLevel.High
            ? highViewportBounds
            : null;
        if (detailLevel == MapDetailLevel.Medium)
        {
            _mediumGeometrySet = geometrySet;
        }
        OnPropertyChanged(nameof(DetailLevel));
        OnPropertyChanged(nameof(DetailLoadError));
        OnPropertyChanged(nameof(GeometrySource));
        OnPropertyChanged(nameof(IsOfficialBoundary));
        OnPropertyChanged(nameof(GeometryBounds));
        OnPropertyChanged(nameof(BoundaryGeometry));
        OnPropertyChanged(nameof(InvalidGeometryCount));
        OnPropertyChanged(nameof(StatusText));
        RebuildLayers();
    }

    private void ApplyMediumFallback(GeoCoordinate? preferredCenter)
    {
        if (_mediumGeometrySet is null || _detailLevel == MapDetailLevel.Medium)
        {
            return;
        }

        ApplyGeometrySet(
            _mediumGeometrySet,
            MapDetailLevel.Medium,
            preferredCenter: preferredCenter);
        TraceDetail("MediumFallback", $"center={FormatCoordinate(preferredCenter)}");
    }

    private static bool Contains(
        MapGeometryBounds outer,
        MapGeometryBounds inner)
    {
        return outer.MinLongitude <= inner.MinLongitude &&
            outer.MaxLongitude >= inner.MaxLongitude &&
            outer.MinLatitude <= inner.MinLatitude &&
            outer.MaxLatitude >= inner.MaxLatitude;
    }

    internal static bool NeedsHighDetailReload(
        MapDetailLevel detailLevel,
        MapGeometryBounds? loadedBounds,
        MapGeometryBounds? requestedBounds)
    {
        loadedBounds = NormalizeHighViewportBounds(loadedBounds);
        requestedBounds = NormalizeHighViewportBounds(requestedBounds);
        return detailLevel == MapDetailLevel.High &&
            requestedBounds is MapGeometryBounds requested &&
            (loadedBounds is not MapGeometryBounds loaded ||
                !Contains(loaded, requested));
    }

    internal static bool ShouldReuseInFlightHighLoad(
        bool isLoading,
        MapDetailLevel? loadingLevel,
        MapGeometryBounds? loadingBounds,
        MapDetailLevel desiredLevel,
        MapGeometryBounds? requestedBounds)
    {
        loadingBounds = NormalizeHighViewportBounds(loadingBounds);
        requestedBounds = NormalizeHighViewportBounds(requestedBounds);
        return isLoading &&
            desiredLevel == MapDetailLevel.High &&
            loadingLevel == MapDetailLevel.High &&
            loadingBounds is MapGeometryBounds loaded &&
            requestedBounds is MapGeometryBounds requested &&
            Contains(loaded, requested);
    }

    internal static bool ShouldReuseInFlightDetailLoad(
        bool isLoading,
        MapDetailLevel? loadingLevel,
        MapDetailLevel desiredLevel)
    {
        return isLoading &&
            desiredLevel != MapDetailLevel.High &&
            loadingLevel == desiredLevel;
    }

    internal static MapGeometryBounds? NormalizeHighViewportBounds(
        MapGeometryBounds? bounds)
    {
        return bounds is MapGeometryBounds value
            ? MapLodResourceProvider.ExpandToHighCacheTile(value)
            : null;
    }

    private MapDetailLevel GetDesiredDetailLevel()
    {
        if (IsDistantEvent)
        {
            return MapDetailLevel.Overview;
        }

        return ZoomLevel > HighDetailZoomThreshold
            ? MapDetailLevel.High
            : ZoomLevel > MediumDetailZoomThreshold
                ? MapDetailLevel.Medium
                : MapDetailLevel.Overview;
    }

    private void RebuildLayers()
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        bool reportChanged = IsViewedReportChanged(report);
        if (reportChanged)
        {
            _focusedCoordinate = null;
            _reportEventId = report?.EventId;
            _reportSourceId = report?.Source.SourceId;
            _reportSourceMessageId = report?.Source.SourceMessageId;
        }

        if (report is null)
        {
            if (reportChanged)
            {
                ClearSelectedObservation();
            }
            Areas = [];
            Municipalities = [];
            Markers = [];
            BoundaryLayers = [];
            UnmappedAreaCount = 0;
            UnmappedMunicipalityCount = 0;
            RaiseLayerProperties();
            if (reportChanged)
            {
                OnPropertyChanged(nameof(ViewedReportKey));
            }

            return;
        }

        report = GetMapReport(report);
        bool isDistantEvent = report.ReportType == EarthquakeReportType.DistantEarthquake;
        if (isDistantEvent)
        {
            report = report with
            {
                MaxIntensity = JmaIntensity.Unknown,
                IntensityAreas = [],
                IntensityMunicipalities = [],
                IntensityStations = [],
            };
        }
        BoundaryLayers = BuildBoundaryLayers(report);

        var geometryByCode = Outline
            .Where(item => item.Code.Length > 0)
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.ToArray(), StringComparer.Ordinal);
        UnmappedAreaCount = 0;
        Areas = report.IntensityAreas
            .Select(area =>
            {
                if (!geometryByCode.TryGetValue(area.Code, out MapPolygonGeometry[]? polygons))
                {
                    UnmappedAreaCount++;
                    return null;
                }

                ImmutableArray<ImmutableArray<GeoCoordinate>> rings = polygons
                    .SelectMany(item => item.Rings.IsDefaultOrEmpty
                        ? [item.Coordinates]
                        : item.Rings)
                    .ToImmutableArray();
                return new EarthquakeMapArea(
                    area.Code,
                    area.Name,
                    area.MaxIntensity,
                    rings[0])
                {
                    PrefectureCode = area.PrefectureCode,
                    Rings = rings,
                };
            })
            .Where(area => area is not null)
            .Select(area => area!)
            .ToArray();

        var municipalityGeometryByCode = (_municipalityGeometry?.Polygons ?? [])
            .Where(item => item.Code.Length > 0)
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.ToArray(), StringComparer.Ordinal);
        UnmappedMunicipalityCount = 0;
        Municipalities = report.IntensityMunicipalities
            .Select(municipality =>
            {
                if (!municipalityGeometryByCode.TryGetValue(
                        municipality.Code,
                        out MapPolygonGeometry[]? polygons))
                {
                    UnmappedMunicipalityCount++;
                    return null;
                }

                ImmutableArray<ImmutableArray<GeoCoordinate>> rings = polygons
                    .SelectMany(item => item.Rings.IsDefaultOrEmpty
                        ? [item.Coordinates]
                        : item.Rings)
                    .ToImmutableArray();
                return new EarthquakeMapMunicipality(
                    municipality.Code,
                    municipality.Name,
                    municipality.MaxIntensity,
                    rings[0])
                {
                    Rings = rings,
                };
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        var markers = new List<EarthquakeMapMarker>();
        if (report.Hypocenter?.Coordinate is GeoCoordinate hypocenter)
        {
            markers.Add(new EarthquakeMapMarker(
                EarthquakeMapMarkerKind.Hypocenter,
                report.Hypocenter.Name ?? "震源",
                hypocenter,
                report.MaxIntensity));
        }

        markers.AddRange(report.IntensityStations
            .Where(station => station.Coordinate is not null &&
                IsKnownIntensity(station.Intensity))
            .OrderBy(station => station.Intensity == JmaIntensity.Unknown
                ? int.MaxValue
                : (int)station.Intensity)
            .Select(station => new EarthquakeMapMarker(
                EarthquakeMapMarkerKind.Station,
                station.Name,
                station.Coordinate!.Value,
                station.Intensity)
            {
                Code = station.Code,
            }));
        Markers = markers;
        RaiseLayerProperties();
        if (reportChanged)
        {
            ClearSelectedObservation();
            OnPropertyChanged(nameof(ViewedReportKey));
        }
    }

    private EarthquakeReport GetMapReport(EarthquakeReport report)
    {
        EarthquakeEvent? selectedEvent = _page.State.SelectedEvent;
        if (selectedEvent is null)
        {
            return report;
        }

        if (!TryGetViewedReportIndex(selectedEvent, report, out int viewedIndex))
        {
            return report;
        }

        IEnumerable<EarthquakeReport> reports = selectedEvent.Reports.Take(viewedIndex + 1);
        EarthquakeReport? areaReport = reports.LastOrDefault(item => !item.IntensityAreas.IsDefaultOrEmpty);
        EarthquakeReport? municipalityReport = reports.LastOrDefault(item => !item.IntensityMunicipalities.IsDefaultOrEmpty);
        EarthquakeReport? stationReport = reports.LastOrDefault(item => !item.IntensityStations.IsDefaultOrEmpty);
        EarthquakeReport? intensityReport = reports.LastOrDefault(item => item.MaxIntensity != JmaIntensity.Unknown);
        EarthquakeReport? hypocenterReport = reports.LastOrDefault(item => item.Hypocenter is not null);

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
            Hypocenter = report.Hypocenter ??
                (report.ReportType == EarthquakeReportType.SeismicIntensity
                    ? hypocenterReport?.Hypocenter
                    : null),
        };
    }

    private EarthquakeReportType GetViewedReportType()
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        if (report is null || report.ReportType != EarthquakeReportType.Hypocenter)
        {
            return report?.ReportType ?? EarthquakeReportType.Unknown;
        }

        EarthquakeEvent? selectedEvent = _page.State.SelectedEvent;
        if (selectedEvent is null)
        {
            return report.ReportType;
        }

        if (!TryGetViewedReportIndex(selectedEvent, report, out int viewedIndex))
        {
            return report.ReportType;
        }

        bool hasDetailedObservations = selectedEvent.Reports
            .Take(viewedIndex + 1)
            .Any(item => item.IntensityStations.Length > 0);
        return hasDetailedObservations
            ? EarthquakeReportType.HypocenterAndIntensity
            : EarthquakeReportType.SeismicIntensity;
    }

    private static bool TryGetViewedReportIndex(
        EarthquakeEvent selectedEvent,
        EarthquakeReport report,
        out int viewedIndex)
    {
        for (int index = 0; index < selectedEvent.Reports.Length; index++)
        {
            EarthquakeReport item = selectedEvent.Reports[index];
            if (string.Equals(item.Source.SourceId, report.Source.SourceId, StringComparison.Ordinal) &&
                string.Equals(
                    item.Source.SourceMessageId,
                    report.Source.SourceMessageId,
                    StringComparison.Ordinal))
            {
                viewedIndex = index;
                return true;
            }
        }

        viewedIndex = -1;
        return false;
    }

    private IReadOnlyList<EarthquakeMapBoundaryLayer> BuildBoundaryLayers(
        EarthquakeReport report)
    {
        if (_boundaryGeometry is null || _boundaryGeometry.Boundaries.IsDefaultOrEmpty)
        {
            return [];
        }

        var intensityByArea = new Dictionary<string, JmaIntensity>(StringComparer.Ordinal);
        foreach (IntensityArea area in report.IntensityAreas)
        {
            string code = area.Code.Trim();
            if (code.Length == 0 || !IsKnownIntensity(area.MaxIntensity))
            {
                continue;
            }

            if (!intensityByArea.TryGetValue(code, out JmaIntensity current) ||
                area.MaxIntensity > current)
            {
                intensityByArea[code] = area.MaxIntensity;
            }
        }

        return _boundaryGeometry.Boundaries
            .GroupBy(boundary => ResolveBoundaryIntensity(boundary, intensityByArea))
            .OrderBy(group => (int)group.Key)
            .Select(group => new EarthquakeMapBoundaryLayer(
                group.Key,
                group.ToImmutableArray()))
            .ToArray();
    }

    private static JmaIntensity ResolveBoundaryIntensity(
        EarthquakeMapBoundary boundary,
        IReadOnlyDictionary<string, JmaIntensity> intensityByArea)
    {
        bool hasFirst = intensityByArea.TryGetValue(
            boundary.AreaCode1,
            out JmaIntensity first);
        JmaIntensity second = JmaIntensity.Unknown;
        bool hasSecond = boundary.AreaCode2.Length > 0 &&
            intensityByArea.TryGetValue(boundary.AreaCode2, out second);

        if (hasFirst && hasSecond)
        {
            return first >= second ? first : second;
        }

        if (hasFirst)
        {
            return first;
        }

        return hasSecond ? second : JmaIntensity.Unknown;
    }

    private static bool IsKnownIntensity(JmaIntensity intensity)
    {
        return intensity is >= JmaIntensity.One and <= JmaIntensity.Seven;
    }

    private void RaiseLayerProperties()
    {
        OnPropertyChanged(nameof(Areas));
        OnPropertyChanged(nameof(Municipalities));
        OnPropertyChanged(nameof(Markers));
        OnPropertyChanged(nameof(BoundaryLayers));
        OnPropertyChanged(nameof(FocusMode));
        OnPropertyChanged(nameof(FollowSelection));
        OnPropertyChanged(nameof(EffectiveFocusMode));
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(IsDistantEvent));
        OnPropertyChanged(nameof(ViewedReportType));
        OnPropertyChanged(nameof(HasDrawableLayers));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FocusedCoordinate));
        OnPropertyChanged(nameof(UnmappedAreaCount));
        OnPropertyChanged(nameof(UnmappedMunicipalityCount));
        OnPropertyChanged(nameof(SelectedAreaHighlights));
        OnPropertyChanged(nameof(SelectedMunicipalityHighlights));
        OnPropertyChanged(nameof(SelectedStationHighlight));
    }

    private static bool TryGetBoundsCenter(
        IReadOnlyList<GeoCoordinate> points,
        out GeoCoordinate coordinate)
    {
        coordinate = default;
        GeoCoordinate[] validPoints = points
            .Where(point => double.IsFinite(point.Latitude) &&
                double.IsFinite(point.Longitude))
            .ToArray();
        if (validPoints.Length == 0)
        {
            return false;
        }

        coordinate = new GeoCoordinate(
            (validPoints.Min(point => point.Latitude) +
                validPoints.Max(point => point.Latitude)) / 2,
            (validPoints.Min(point => point.Longitude) +
                validPoints.Max(point => point.Longitude)) / 2);
        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private static void TraceDetail(string action, string detail)
    {
        string message = $"[MapDebug] {DateTimeOffset.Now:HH:mm:ss.fff} LOD {action} {detail}";
        Console.WriteLine(message);
    }

    private static string FormatCoordinate(GeoCoordinate? coordinate) =>
        coordinate is GeoCoordinate value
            ? $"{value.Latitude:0.####},{value.Longitude:0.####}"
            : "null";

    private static string FormatBounds(MapGeometryBounds? bounds) =>
        bounds is MapGeometryBounds value
            ? $"[{value.MinLongitude:0.###},{value.MaxLongitude:0.###}]x[{value.MinLatitude:0.###},{value.MaxLatitude:0.###}]"
            : "null";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
