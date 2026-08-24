using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.Views;

public partial class EarthquakeMapView : UserControl
{
    private static readonly Color OutlineFill = Color.FromRgb(243, 239, 228);
    private static readonly Color OutlineStroke = Color.FromRgb(121, 143, 153);
    private bool _renderPending;
    private bool _isPanning;
    private Point _lastPanPoint;
    private Vector _panOffset;

    public EarthquakeMapView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private EarthquakeMapViewModel? ViewModel => DataContext as EarthquakeMapViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RenderMap();
        await EnsureMapDetailLevelAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPanning();
        _panOffset = default;
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RequestRender();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EarthquakeMapViewModel.FocusedCoordinate))
        {
            _panOffset = default;
        }

        if (ViewModel?.FollowSelection == true &&
            e.PropertyName is nameof(EarthquakeMapViewModel.FollowSelection)
                or nameof(EarthquakeMapViewModel.EffectiveFocusMode)
                or nameof(EarthquakeMapViewModel.HasSelectedEvent))
        {
            _panOffset = default;
        }

        if (e.PropertyName is nameof(EarthquakeMapViewModel.Areas)
            or nameof(EarthquakeMapViewModel.Municipalities)
            or nameof(EarthquakeMapViewModel.BoundaryLayers)
            or nameof(EarthquakeMapViewModel.Markers)
            or nameof(EarthquakeMapViewModel.ZoomLevel)
            or nameof(EarthquakeMapViewModel.FocusedCoordinate)
            or nameof(EarthquakeMapViewModel.FollowSelection)
            or nameof(EarthquakeMapViewModel.EffectiveFocusMode)
            or nameof(EarthquakeMapViewModel.HasSelectedEvent))
        {
            RequestRender();
        }

        if (e.PropertyName == nameof(EarthquakeMapViewModel.ZoomLevel))
        {
            _ = EnsureMapDetailLevelAsync();
        }
    }

    private void RequestRender()
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderPending = false;
            RenderMap();
        }));
    }

    private async void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ZoomIn();
        await EnsureMapDetailLevelAsync();
    }

    private async void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ZoomOut();
        await EnsureMapDetailLevelAsync();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        _panOffset = default;
        ViewModel?.ResetView();
    }

    private async void OnFocusSelectedClick(object sender, RoutedEventArgs e)
    {
        _panOffset = default;
        ViewModel?.FocusSelectedEvent();
        await EnsureMapDetailLevelAsync();
    }

    private void OnFollowSelectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is CheckBox checkBox)
        {
            if (checkBox.IsChecked == true)
            {
                _panOffset = default;
            }

            ViewModel.SetFollowSelection(checkBox.IsChecked == true);
        }
    }

    private void OnMapMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ViewModel is null)
        {
            return;
        }

        ViewModel.BeginManualInteraction();
        _isPanning = true;
        _lastPanPoint = e.GetPosition(MapCanvas);
        MapCanvas.CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnMapMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || !MapCanvas.IsMouseCaptured)
        {
            return;
        }

        Point currentPoint = e.GetPosition(MapCanvas);
        Vector delta = currentPoint - _lastPanPoint;
        if (delta.LengthSquared > 0)
        {
            _panOffset += delta;
            _lastPanPoint = currentPoint;
            RequestRender();
        }

        e.Handled = true;
    }

    private void OnMapMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_isPanning)
        {
            return;
        }

        StopPanning();
        e.Handled = true;
    }

    private void OnMapLostMouseCapture(object sender, MouseEventArgs e)
    {
        StopPanning();
    }

    private async void OnMapMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel is null || MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return;
        }

        Point anchor = e.GetPosition(MapCanvas);
        ViewModel.BeginManualInteraction();
        MapProjection before = CreateProjection();
        GeoCoordinate anchorCoordinate = before.Unproject(anchor);
        double previousZoom = ViewModel.ZoomLevel;
        if (e.Delta > 0)
        {
            ViewModel.ZoomIn();
        }
        else if (e.Delta < 0)
        {
            ViewModel.ZoomOut();
        }

        if (Math.Abs(previousZoom - ViewModel.ZoomLevel) >= 0.001)
        {
            MapProjection after = CreateProjection();
            Point projectedAnchor = after.Project(anchorCoordinate);
            _panOffset += new Vector(
                anchor.X - projectedAnchor.X,
                anchor.Y - projectedAnchor.Y);
            RequestRender();
        }

        await EnsureMapDetailLevelAsync();

        e.Handled = true;
    }

    private async Task EnsureMapDetailLevelAsync()
    {
        if (ViewModel is null || !IsLoaded)
        {
            return;
        }

        try
        {
            await ViewModel.EnsureDetailLevelForZoomAsync();
        }
        catch (ObjectDisposedException)
        {
            // 控件卸载期间忽略异步加载竞态。
        }
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        if (MapCanvas.IsMouseCaptured)
        {
            MapCanvas.ReleaseMouseCapture();
        }

        Cursor = null;
    }

    private void RenderMap()
    {
        if (!IsLoaded || ViewModel is null || MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return;
        }

        MapCanvas.Children.Clear();
        GeoCoordinate? selectedEventFocusCoordinate =
            ViewModel.TryGetSelectedEventFocusCoordinate(out GeoCoordinate eventFocus)
                ? eventFocus
                : null;
        MapProjection projection = CreateProjection(selectedEventFocusCoordinate);
        bool drawBaseOutlineStroke = ViewModel.BoundaryLayers.Count == 0;

        foreach (MapPolygonGeometry polygon in ViewModel.Outline)
        {
            var shape = new Path
            {
                Data = ToPathGeometry(GetRings(polygon.Rings, polygon.Coordinates), projection),
                Fill = new SolidColorBrush(OutlineFill),
                Stroke = drawBaseOutlineStroke
                    ? new SolidColorBrush(OutlineStroke)
                    : null,
                StrokeThickness = 1,
                ToolTip = polygon.Name,
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapMunicipality municipality in ViewModel.Municipalities)
        {
            bool hasIntensity = IsKnownIntensity(municipality.Intensity);

            var shape = new Path
            {
                Data = ToPathGeometry(
                    GetRings(municipality.Rings, municipality.Coordinates),
                    projection),
                Fill = hasIntensity
                    ? new SolidColorBrush(GetIntensityColor(municipality.Intensity, 150))
                    : null,
                Stroke = hasIntensity
                    ? new SolidColorBrush(Color.FromArgb(225, 42, 50, 55))
                    : new SolidColorBrush(OutlineStroke),
                StrokeThickness = 0.8,
                ToolTip = hasIntensity
                    ? $"{municipality.Name} · 震度 {GetIntensityText(municipality.Intensity)}"
                    : null,
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapBoundaryLayer layer in ViewModel.BoundaryLayers)
        {
            if (layer.Boundaries.Length == 0)
            {
                continue;
            }

            var shape = new Path
            {
                Data = ToBoundaryPathGeometry(layer.Boundaries, projection),
                Stroke = new SolidColorBrush(GetIntensityColor(layer.Intensity, 245)),
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapMarker marker in ViewModel.Markers)
        {
            DrawMarker(marker, projection);
        }
    }

    private MapProjection CreateProjection(GeoCoordinate? selectedEventFocusCoordinate = null)
    {
        EarthquakeMapViewModel viewModel = ViewModel!;
        if (selectedEventFocusCoordinate is null &&
            viewModel.TryGetSelectedEventFocusCoordinate(out GeoCoordinate eventFocus))
        {
            selectedEventFocusCoordinate = eventFocus;
        }

        return MapProjection.Create(
            viewModel.Outline,
            viewModel.Municipalities,
            viewModel.Markers,
            viewModel.EffectiveFocusMode,
            viewModel.FocusedCoordinate,
            selectedEventFocusCoordinate,
            viewModel.ZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight,
            _panOffset.X,
            _panOffset.Y);
    }

    private static IReadOnlyList<ImmutableArray<GeoCoordinate>> GetRings(
        IReadOnlyList<ImmutableArray<GeoCoordinate>> rings,
        ImmutableArray<GeoCoordinate> coordinates)
    {
        return rings.Count > 0 ? rings : [coordinates];
    }

    private static StreamGeometry ToPathGeometry(
        IReadOnlyList<ImmutableArray<GeoCoordinate>> rings,
        MapProjection projection)
    {
        var geometry = new StreamGeometry
        {
            FillRule = FillRule.EvenOdd,
        };
        using (StreamGeometryContext context = geometry.Open())
        {
            foreach (IReadOnlyList<GeoCoordinate> ring in rings)
            {
                if (ring.Count < 3)
                {
                    continue;
                }

                context.BeginFigure(projection.Project(ring[0]), true, true);
                for (int index = 1; index < ring.Count; index++)
                {
                    context.LineTo(projection.Project(ring[index]), true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry ToBoundaryPathGeometry(
        IReadOnlyList<EarthquakeMapBoundary> boundaries,
        MapProjection projection)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            foreach (EarthquakeMapBoundary boundary in boundaries)
            {
                if (boundary.Coordinates.Length < 2)
                {
                    continue;
                }

                context.BeginFigure(
                    projection.Project(boundary.Coordinates[0]),
                    isFilled: false,
                    isClosed: false);
                for (int index = 1; index < boundary.Coordinates.Length; index++)
                {
                    context.LineTo(
                        projection.Project(boundary.Coordinates[index]),
                        isStroked: true,
                        isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawMarker(EarthquakeMapMarker marker, MapProjection projection)
    {
        Point point = projection.Project(marker.Coordinate);
        double size = marker.Kind == EarthquakeMapMarkerKind.Hypocenter ? 15 : 8;
        var shape = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? new SolidColorBrush(Color.FromRgb(190, 61, 52))
                : new SolidColorBrush(GetIntensityColor(marker.Intensity, 245)),
            Stroke = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(GetIntensityBorderColor(marker.Intensity, 245)),
            StrokeThickness = 1.5,
            ToolTip = $"{marker.Label} · 震度 {GetIntensityText(marker.Intensity)}",
        };
        Canvas.SetLeft(shape, point.X - size / 2);
        Canvas.SetTop(shape, point.Y - size / 2);
        MapCanvas.Children.Add(shape);
    }

    private static Color GetIntensityColor(JmaIntensity intensity, byte alpha)
    {
        Color color = intensity switch
        {
            JmaIntensity.One => Color.FromRgb(120, 199, 216),
            JmaIntensity.Two => Color.FromRgb(112, 214, 193),
            JmaIntensity.Three => Color.FromRgb(240, 201, 67),
            JmaIntensity.Four => Color.FromRgb(232, 154, 60),
            JmaIntensity.FiveLower => Color.FromRgb(232, 94, 63),
            JmaIntensity.FiveUpper => Color.FromRgb(240, 68, 56),
            JmaIntensity.SixLower => Color.FromRgb(216, 27, 96),
            JmaIntensity.SixUpper => Color.FromRgb(142, 36, 170),
            JmaIntensity.Seven => Color.FromRgb(74, 20, 140),
            _ => Color.FromRgb(230, 235, 239),
        };
        color.A = alpha;
        return color;
    }

    private static bool IsKnownIntensity(JmaIntensity intensity)
    {
        return intensity is >= JmaIntensity.One and <= JmaIntensity.Seven;
    }

    private static Color GetIntensityBorderColor(JmaIntensity intensity, byte alpha)
    {
        Color fill = GetIntensityColor(intensity, 255);
        double luminance =
            (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255;
        Color color = luminance < 0.55
            ? Colors.White
            : Color.FromRgb(57, 69, 76);
        color.A = alpha;
        return color;
    }

    private static string GetIntensityText(JmaIntensity intensity)
    {
        return intensity switch
        {
            JmaIntensity.FiveLower => "5弱",
            JmaIntensity.FiveUpper => "5强",
            JmaIntensity.SixLower => "6弱",
            JmaIntensity.SixUpper => "6强",
            JmaIntensity.Unknown => "不明",
            _ => intensity.ToCode(),
        };
    }

    private sealed class MapProjection
    {
        private readonly double _scale;
        private readonly double _longitudeScaleFactor;
        private readonly double _centerLongitude;
        private readonly double _centerLatitude;
        private readonly double _width;
        private readonly double _height;
        private readonly double _panX;
        private readonly double _panY;

        private MapProjection(
            double scale,
            double longitudeScaleFactor,
            double centerLongitude,
            double centerLatitude,
            double width,
            double height,
            double panX,
            double panY)
        {
            _scale = scale;
            _longitudeScaleFactor = longitudeScaleFactor;
            _centerLongitude = centerLongitude;
            _centerLatitude = centerLatitude;
            _width = width;
            _height = height;
            _panX = panX;
            _panY = panY;
        }

        public static MapProjection Create(
            IReadOnlyList<MapPolygonGeometry> outline,
            IReadOnlyList<EarthquakeMapMunicipality> municipalities,
            IReadOnlyList<EarthquakeMapMarker> markers,
            EarthquakeMapFocusMode focusMode,
            GeoCoordinate? focusedCoordinate,
            GeoCoordinate? selectedEventFocusCoordinate,
            double zoomLevel,
            double width,
            double height,
            double panX,
            double panY)
        {
            MapGeometryBounds bounds = GetBounds(outline, municipalities, markers);
            double centerLongitude = (bounds.MinLongitude + bounds.MaxLongitude) / 2;
            double centerLatitude = (bounds.MinLatitude + bounds.MaxLatitude) / 2;
            if (focusedCoordinate is GeoCoordinate location)
            {
                centerLongitude = location.Longitude;
                centerLatitude = location.Latitude;
            }
            else if (focusMode == EarthquakeMapFocusMode.SelectedEvent &&
                selectedEventFocusCoordinate is GeoCoordinate eventFocus)
            {
                centerLongitude = eventFocus.Longitude;
                centerLatitude = eventFocus.Latitude;
            }

            double longitudeScaleFactor = Math.Max(
                0.2,
                Math.Cos(centerLatitude * Math.PI / 180));
            double scale = Math.Min(
                (width - 48) / (bounds.LongitudeSpan * longitudeScaleFactor),
                (height - 48) / bounds.LatitudeSpan);
            return new MapProjection(
                scale * Math.Max(1, zoomLevel),
                longitudeScaleFactor,
                centerLongitude,
                centerLatitude,
                width,
                height,
                panX,
                panY);
        }

        private static MapGeometryBounds GetBounds(
            IReadOnlyList<MapPolygonGeometry> outline,
            IReadOnlyList<EarthquakeMapMunicipality> municipalities,
            IReadOnlyList<EarthquakeMapMarker> markers)
        {
            IEnumerable<GeoCoordinate> coordinates = outline
                .SelectMany(item => item.Rings.IsDefaultOrEmpty
                    ? [item.Coordinates]
                    : item.Rings)
                .SelectMany(item => item)
                .Concat(municipalities
                    .SelectMany(item => item.Rings)
                    .SelectMany(item => item))
                .Concat(markers.Select(item => item.Coordinate));
            if (!coordinates.Any())
            {
                return new MapGeometryBounds(126, 147, 24, 47);
            }

            coordinates = coordinates.ToArray();
            return new MapGeometryBounds(
                coordinates.Min(item => item.Longitude),
                coordinates.Max(item => item.Longitude),
                coordinates.Min(item => item.Latitude),
                coordinates.Max(item => item.Latitude));
        }

        public Point Project(GeoCoordinate coordinate)
        {
            return new Point(
                _width / 2 + (coordinate.Longitude - _centerLongitude) * _scale * _longitudeScaleFactor + _panX,
                _height / 2 - (coordinate.Latitude - _centerLatitude) * _scale + _panY);
        }

        public GeoCoordinate Unproject(Point point)
        {
            return new GeoCoordinate(
                _centerLatitude -
                    (point.Y - _height / 2 - _panY) / _scale,
                _centerLongitude +
                    (point.X - _width / 2 - _panX) /
                    (_scale * _longitudeScaleFactor));
        }
    }
}
