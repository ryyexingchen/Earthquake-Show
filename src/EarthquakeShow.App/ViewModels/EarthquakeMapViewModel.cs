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

public sealed record EarthquakeMapMarker(
    EarthquakeMapMarkerKind Kind,
    string Label,
    GeoCoordinate Coordinate,
    JmaIntensity Intensity);

public sealed class EarthquakeMapViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EarthquakePageViewModel _page;
    private readonly OfflineMapGeometry _geometry;
    private double _zoomLevel = 1;
    private GeoCoordinate? _focusedCoordinate;
    private string? _reportSourceId;
    private string? _reportSourceMessageId;
    private bool _isDisposed;

    public EarthquakeMapViewModel(
        EarthquakePageViewModel page,
        OfflineMapGeometry geometry)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _page.PropertyChanged += OnPagePropertyChanged;
        RebuildLayers();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<MapPolygonGeometry> Outline => _geometry.Polygons;

    public string GeometrySource => _geometry.Source;

    public bool IsOfficialBoundary => _geometry.IsOfficialBoundary;

    public MapGeometryBounds GeometryBounds => _geometry.Bounds;

    public int InvalidGeometryCount => _geometry.InvalidGeometryCount;

    public int UnmappedAreaCount { get; private set; }

    public IReadOnlyList<EarthquakeMapArea> Areas { get; private set; } = [];

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

    public bool HasDrawableLayers => Areas.Count > 0 || Markers.Count > 0;

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

    public bool TryGetSelectedEventFocusCoordinate(out GeoCoordinate coordinate)
    {
        IReadOnlyList<GeoCoordinate> points = Markers
            .Select(marker => marker.Coordinate)
            .Concat(Areas.SelectMany(area => area.Rings.SelectMany(ring => ring)))
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
        ZoomLevel = Math.Min(4, ZoomLevel * 1.25);
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
        _page.SetMapViewState(_page.State.Map with
        {
            FollowSelection = followSelection,
        });
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
            Markers = [];
            UnmappedAreaCount = 0;
            RaiseLayerProperties();
            return;
        }

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

    private void RaiseLayerProperties()
    {
        OnPropertyChanged(nameof(Areas));
        OnPropertyChanged(nameof(Markers));
        OnPropertyChanged(nameof(FocusMode));
        OnPropertyChanged(nameof(FollowSelection));
        OnPropertyChanged(nameof(EffectiveFocusMode));
        OnPropertyChanged(nameof(HasSelectedEvent));
        OnPropertyChanged(nameof(HasDrawableLayers));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FocusedCoordinate));
        OnPropertyChanged(nameof(UnmappedAreaCount));
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
