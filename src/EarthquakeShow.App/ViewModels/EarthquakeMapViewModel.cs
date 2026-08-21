using System.Collections.Immutable;
using System.ComponentModel;
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

public sealed class EarthquakeMapViewModel : INotifyPropertyChanged, IDisposable
{
    public const double MaximumZoomLevel = 12;

    private readonly EarthquakePageViewModel _page;
    private readonly OfflineMapGeometry _geometry;
    private readonly OfflineMapGeometry? _municipalityGeometry;
    private readonly OfflineMapBoundaryGeometry? _boundaryGeometry;
    private double _zoomLevel = 1;
    private GeoCoordinate? _focusedCoordinate;
    private string? _reportSourceId;
    private string? _reportSourceMessageId;
    private bool _isDisposed;

    public EarthquakeMapViewModel(
        EarthquakePageViewModel page,
        OfflineMapGeometry geometry,
        OfflineMapGeometry? municipalityGeometry = null,
        OfflineMapBoundaryGeometry? boundaryGeometry = null)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _municipalityGeometry = municipalityGeometry;
        _boundaryGeometry = boundaryGeometry;
        _page.PropertyChanged += OnPagePropertyChanged;
        RebuildLayers();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<MapPolygonGeometry> Outline => _geometry.Polygons;

    public string GeometrySource => _geometry.Source;

    public bool IsOfficialBoundary => _geometry.IsOfficialBoundary;

    public MapGeometryBounds GeometryBounds => _geometry.Bounds;

    public OfflineMapBoundaryGeometry? BoundaryGeometry => _boundaryGeometry;

    public int InvalidGeometryCount => _geometry.InvalidGeometryCount;

    public int UnmappedAreaCount { get; private set; }

    public int UnmappedMunicipalityCount { get; private set; }

    public IReadOnlyList<EarthquakeMapArea> Areas { get; private set; } = [];

    public IReadOnlyList<EarthquakeMapMunicipality> Municipalities { get; private set; } = [];

    public IReadOnlyList<EarthquakeMapBoundaryLayer> BoundaryLayers { get; private set; } = [];

    public IReadOnlyList<EarthquakeMapMarker> Markers { get; private set; } = [];

    public GeoCoordinate? FocusedCoordinate => _focusedCoordinate;

    public double ZoomLevel
    {
        get => _zoomLevel;
        private set
        {
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
        IReadOnlyList<GeoCoordinate> points = Markers
            .Select(marker => marker.Coordinate)
            .Concat(Areas.SelectMany(area => area.Rings.SelectMany(ring => ring)))
            .Concat(Municipalities.SelectMany(item => item.Rings.SelectMany(ring => ring)))
            .ToArray();
        return TryGetBoundsCenter(points, out coordinate);
    }

    public string StatusText
    {
        get
        {
            if (!IsOfficialBoundary)
            {
                return HasSelectedEvent
                    ? "离线示意底图 · 当前事件图层"
                    : "离线示意底图 · 未选择事件";
            }

            return HasSelectedEvent ? "离线地图 · 当前事件图层" : "离线地图 · 未选择事件";
        }
    }

    public void ZoomIn()
    {
        ThrowIfDisposed();
        ZoomLevel = Math.Min(MaximumZoomLevel, ZoomLevel * 1.25);
    }

    public void ZoomOut()
    {
        ThrowIfDisposed();
        ZoomLevel = Math.Max(1, ZoomLevel / 1.25);
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
        });
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
            RebuildLayers();
        }
    }

    private void RebuildLayers()
    {
        EarthquakeReport? report = _page.State.ViewedReport;
        if (!string.Equals(_reportSourceId, report?.Source.SourceId, StringComparison.Ordinal) ||
            !string.Equals(
                _reportSourceMessageId,
                report?.Source.SourceMessageId,
                StringComparison.Ordinal))
        {
            _focusedCoordinate = null;
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
            return;
        }

        report = GetMapReport(report);
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
