using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
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

    public EarthquakeMapView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private EarthquakeMapViewModel? ViewModel => DataContext as EarthquakeMapViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RenderMap();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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
        if (e.PropertyName is nameof(EarthquakeMapViewModel.Areas)
            or nameof(EarthquakeMapViewModel.Markers)
            or nameof(EarthquakeMapViewModel.ZoomLevel)
            or nameof(EarthquakeMapViewModel.FocusedCoordinate)
            or nameof(EarthquakeMapViewModel.EffectiveFocusMode)
            or nameof(EarthquakeMapViewModel.HasSelectedEvent))
        {
            RequestRender();
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

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ZoomIn();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ZoomOut();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ResetView();
    }

    private void OnFocusSelectedClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.FocusSelectedEvent();
    }

    private void OnFollowSelectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is CheckBox checkBox)
        {
            ViewModel.SetFollowSelection(checkBox.IsChecked == true);
        }
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
        MapProjection projection = MapProjection.Create(
            ViewModel.Outline,
            ViewModel.Municipalities,
            ViewModel.Markers,
            ViewModel.EffectiveFocusMode,
            ViewModel.FocusedCoordinate,
            selectedEventFocusCoordinate,
            ViewModel.ZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);

        foreach (MapPolygonGeometry polygon in ViewModel.Outline)
        {
            var shape = new Path
            {
                Data = ToPathGeometry(GetRings(polygon.Rings, polygon.Coordinates), projection),
                Fill = new SolidColorBrush(OutlineFill),
                Stroke = new SolidColorBrush(OutlineStroke),
                StrokeThickness = 1,
                ToolTip = polygon.Name,
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapArea area in ViewModel.Areas)
        {
            var shape = new Path
            {
                Data = ToPathGeometry(GetRings(area.Rings, area.Coordinates), projection),
                Fill = new SolidColorBrush(GetIntensityColor(area.Intensity, 180)),
                Stroke = new SolidColorBrush(GetIntensityBorderColor(area.Intensity, 235)),
                StrokeThickness = 1.2,
                ToolTip = $"{area.Name} · 震度 {GetIntensityText(area.Intensity)}",
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapMunicipality municipality in ViewModel.Municipalities)
        {
            var shape = new Path
            {
                Data = ToPathGeometry(
                    GetRings(municipality.Rings, municipality.Coordinates),
                    projection),
                Fill = new SolidColorBrush(GetIntensityColor(municipality.Intensity, 95)),
                Stroke = new SolidColorBrush(GetIntensityBorderColor(municipality.Intensity, 190)),
                StrokeThickness = 0.8,
                ToolTip = $"{municipality.Name} · 震度 {GetIntensityText(municipality.Intensity)}",
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapMarker marker in ViewModel.Markers)
        {
            DrawMarker(marker, projection);
        }
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
            JmaIntensity.One => Color.FromRgb(184, 230, 242),
            JmaIntensity.Two => Color.FromRgb(112, 214, 193),
            JmaIntensity.Three => Color.FromRgb(255, 228, 94),
            JmaIntensity.Four => Color.FromRgb(255, 179, 71),
            JmaIntensity.FiveLower => Color.FromRgb(255, 122, 69),
            JmaIntensity.FiveUpper => Color.FromRgb(240, 68, 56),
            JmaIntensity.SixLower => Color.FromRgb(216, 27, 96),
            JmaIntensity.SixUpper => Color.FromRgb(142, 36, 170),
            JmaIntensity.Seven => Color.FromRgb(74, 20, 140),
            _ => Color.FromRgb(230, 235, 239),
        };
        color.A = alpha;
        return color;
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

        private MapProjection(
            double scale,
            double longitudeScaleFactor,
            double centerLongitude,
            double centerLatitude,
            double width,
            double height)
        {
            _scale = scale;
            _longitudeScaleFactor = longitudeScaleFactor;
            _centerLongitude = centerLongitude;
            _centerLatitude = centerLatitude;
            _width = width;
            _height = height;
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
            double height)
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
                height);
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
                _width / 2 + (coordinate.Longitude - _centerLongitude) * _scale * _longitudeScaleFactor,
                _height / 2 - (coordinate.Latitude - _centerLatitude) * _scale);
        }
    }
}
