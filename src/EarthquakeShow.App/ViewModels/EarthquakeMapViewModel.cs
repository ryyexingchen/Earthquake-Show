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

public sealed record EarthquakeMapArea(
    string Code,
    string Name,
    JmaIntensity Intensity,
    ImmutableArray<GeoCoordinate> Coordinates)
{
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
    JmaIntensity Intensity);

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
    private string? _detailLoadError;
    private MapDetailLevel? _detailLoadErrorLevel;
    private MapGeometryBounds? _highLoadedViewportBounds;
    private MapGeometrySet? _mediumGeometrySet;
    private MapGeometrySet? _retainedHighGeometrySet;
    private MapGeometryBounds? _retainedHighViewportBounds;
    private long _detailLoadGeneration;
    private bool _lastHighLoadUsedCache;
    private bool _isApplyingAutoScale;
    private bool _isDisposed;

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

    public GeoCoordinate? FocusedCoordinate => _focusedCoordinate;

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

        EarthquakeMapArea? area = Areas.FirstOrDefault(item =>
            string.Equals(item.Code, areaCode, StringComparison.Ordinal));
        if (area is null)
        {
            return false;
        }

        IReadOnlyList<GeoCoordinate> points = area.Rings
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

        EarthquakeMapMunicipality? municipality = Municipalities.FirstOrDefault(item =>
            string.Equals(item.Code, municipalityCode, StringComparison.Ordinal));
        if (municipality is null)
        {
            return false;
        }

        IReadOnlyList<GeoCoordinate> points = municipality.Rings
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
        bool preferMedium = false,
        MapGeometryBounds? viewportBounds = null,
        GeoCoordinate? viewportCenter = null)
    {
        ThrowIfDisposed();
        MapDetailLevel desiredLevel = GetDesiredDetailLevel(preferMedium);
        TraceDetail(
            "EnsureStart",
            $"generation={_detailLoadGeneration} desired={desiredLevel} current={_detailLevel} " +
            $"viewport={FormatBounds(viewportBounds)} center={FormatCoordinate(viewportCenter)}");
        if (desiredLevel == MapDetailLevel.Overview)
        {
            _detailLoadCancellation?.Cancel();
            _detailLoadGeneration++;
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

        if (_detailLevel == desiredLevel)
        {
            if (!NeedsHighDetailReload(
                    desiredLevel,
                    _highLoadedViewportBounds,
                    viewportBounds))
            {
                TraceDetail("EnsureReuseCurrent", $"detail={_detailLevel} viewport={FormatBounds(viewportBounds)}");
                return;
            }
        }

        if (desiredLevel == MapDetailLevel.High &&
            viewportBounds is MapGeometryBounds highBounds &&
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
        _lastHighLoadUsedCache = false;
        IsLoadingDetail = true;
        try
        {
            if (desiredLevel == MapDetailLevel.High &&
                _mediumGeometrySet is null)
            {
                MapGeometrySet mediumGeometrySet = await Task.Run(
                    () => _lodResourceProvider.LoadMedium(loadCancellation.Token),
                    loadCancellation.Token);
                loadCancellation.Token.ThrowIfCancellationRequested();
                if (loadGeneration != _detailLoadGeneration || _isDisposed)
                {
                    return;
                }

                _mediumGeometrySet = mediumGeometrySet;
                TraceDetail("MediumLoaded", $"generation={loadGeneration}");
            }

            if (desiredLevel == MapDetailLevel.High &&
                _detailLevel != MapDetailLevel.Medium)
            {
                await Task.Yield();
                if (loadGeneration != _detailLoadGeneration ||
                    loadCancellation.IsCancellationRequested ||
                    _isDisposed)
                {
                    return;
                }

                ApplyMediumFallback(viewportCenter);
                await Task.Yield();
                if (loadGeneration != _detailLoadGeneration ||
                    loadCancellation.IsCancellationRequested ||
                    _isDisposed)
                {
                    return;
                }
            }

            MapGeometrySet geometrySet = await Task.Run(
                () => desiredLevel == MapDetailLevel.High
                    ? _lodResourceProvider.LoadHigh(
                        loadCancellation.Token,
                        viewportBounds)
                    : _lodResourceProvider.LoadMedium(loadCancellation.Token),
                loadCancellation.Token);
            loadCancellation.Token.ThrowIfCancellationRequested();
            if (_isDisposed || loadGeneration != _detailLoadGeneration)
            {
                return;
            }

            ApplyGeometrySet(
                geometrySet,
                desiredLevel,
                viewportBounds,
                viewportCenter);
            TraceDetail("LoadApplied", $"generation={loadGeneration} detail={desiredLevel} center={FormatCoordinate(viewportCenter)}");
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
                IsLoadingDetail = false;
            }

            loadCancellation.Dispose();
        }
    }

    internal bool WillChangeDetailLevel(
        bool preferMedium,
        MapGeometryBounds? viewportBounds)
    {
        MapDetailLevel desiredLevel = GetDesiredDetailLevel(preferMedium);
        return desiredLevel != DetailLevel ||
            NeedsHighDetailReload(
                desiredLevel,
                _highLoadedViewportBounds,
                viewportBounds);
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
        _isDisposed = true;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(EarthquakePageViewModel.State))
        {
            if (!_isApplyingAutoScale && IsViewedReportChanged(_page.State.ViewedReport))
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

            RebuildLayers();
        }
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
        return detailLevel == MapDetailLevel.High &&
            requestedBounds is MapGeometryBounds requested &&
            (loadedBounds is not MapGeometryBounds loaded ||
                !Contains(loaded, requested));
    }

    private MapDetailLevel GetDesiredDetailLevel(bool preferMedium)
    {
        if (IsDistantEvent)
        {
            return MapDetailLevel.Overview;
        }

        return preferMedium
            ? MapDetailLevel.Medium
            : ZoomLevel > HighDetailZoomThreshold
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
            .Where(station => station.Coordinate is not null)
            .OrderBy(station => station.Intensity == JmaIntensity.Unknown
                ? int.MaxValue
                : (int)station.Intensity)
            .Select(station => new EarthquakeMapMarker(
                EarthquakeMapMarkerKind.Station,
                station.Name,
                station.Coordinate!.Value,
                station.Intensity)));
        Markers = markers;
        RaiseLayerProperties();
        if (reportChanged)
        {
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

        int viewedIndex = selectedEvent.Reports
            .Select((item, index) => (item, index))
            .FirstOrDefault(item =>
                string.Equals(item.item.Source.SourceId, report.Source.SourceId, StringComparison.Ordinal) &&
                string.Equals(item.item.Source.SourceMessageId, report.Source.SourceMessageId, StringComparison.Ordinal))
            .index;
        IEnumerable<EarthquakeReport> reports = selectedEvent.Reports.Take(viewedIndex + 1);
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
        OnPropertyChanged(nameof(HasDrawableLayers));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FocusedCoordinate));
        OnPropertyChanged(nameof(UnmappedAreaCount));
        OnPropertyChanged(nameof(UnmappedMunicipalityCount));
    }

    private static bool TryGetBoundsCenter(
        IReadOnlyList<GeoCoordinate> points,
        out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (points.Count == 0)
        {
            return false;
        }

        coordinate = new GeoCoordinate(
            (points.Min(point => point.Latitude) + points.Max(point => point.Latitude)) / 2,
            (points.Min(point => point.Longitude) + points.Max(point => point.Longitude)) / 2);
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
        Debug.WriteLine(message);
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
