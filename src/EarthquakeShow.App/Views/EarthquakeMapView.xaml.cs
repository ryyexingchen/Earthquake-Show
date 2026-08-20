using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.Views;

public partial class EarthquakeMapView : UserControl
{
    private static readonly Color OutlineFill = Color.FromRgb(223, 232, 234);
    private static readonly Color OutlineStroke = Color.FromRgb(145, 162, 168);

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
        RenderMap();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(RenderMap);
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
        MapProjection projection = MapProjection.Create(
            ViewModel.Outline,
            ViewModel.Markers,
            ViewModel.EffectiveFocusMode,
            ViewModel.FocusedCoordinate,
            ViewModel.ZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);

        foreach (MapPolygonGeometry polygon in ViewModel.Outline)
        {
            var shape = new Polygon
            {
                Points = ToPointCollection(polygon.Coordinates, projection),
                Fill = new SolidColorBrush(OutlineFill),
                Stroke = new SolidColorBrush(OutlineStroke),
                StrokeThickness = 1,
                ToolTip = polygon.Name,
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapArea area in ViewModel.Areas)
        {
            var shape = new Polygon
            {
                Points = ToPointCollection(area.Coordinates, projection),
                Fill = new SolidColorBrush(GetIntensityColor(area.Intensity, 180)),
                Stroke = new SolidColorBrush(GetIntensityColor(area.Intensity, 235)),
                StrokeThickness = 1.2,
                ToolTip = $"{area.Name} · 震度 {GetIntensityText(area.Intensity)}",
            };
            MapCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapMarker marker in ViewModel.Markers)
        {
            DrawMarker(marker, projection);
        }
    }

    private static PointCollection ToPointCollection(
        IReadOnlyList<GeoCoordinate> coordinates,
        MapProjection projection)
    {
        var points = new PointCollection();
        foreach (GeoCoordinate coordinate in coordinates)
        {
            points.Add(projection.Project(coordinate));
        }

        return points;
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
            Stroke = new SolidColorBrush(Colors.White),
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
            JmaIntensity.One => Color.FromRgb(220, 239, 247),
            JmaIntensity.Two => Color.FromRgb(191, 232, 219),
            JmaIntensity.Three => Color.FromRgb(244, 229, 156),
            JmaIntensity.Four => Color.FromRgb(244, 195, 125),
            JmaIntensity.FiveLower => Color.FromRgb(235, 155, 118),
            JmaIntensity.FiveUpper => Color.FromRgb(225, 116, 103),
            JmaIntensity.SixLower => Color.FromRgb(201, 85, 103),
            JmaIntensity.SixUpper => Color.FromRgb(150, 63, 104),
            JmaIntensity.Seven => Color.FromRgb(93, 49, 93),
            _ => Color.FromRgb(225, 229, 231),
        };
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
        private const double MinLongitude = 126;
        private const double MaxLongitude = 147;
        private const double MinLatitude = 24;
        private const double MaxLatitude = 47;
        private readonly double _scale;
        private readonly double _centerLongitude;
        private readonly double _centerLatitude;
        private readonly double _width;
        private readonly double _height;

        private MapProjection(
            double scale,
            double centerLongitude,
            double centerLatitude,
            double width,
            double height)
        {
            _scale = scale;
            _centerLongitude = centerLongitude;
            _centerLatitude = centerLatitude;
            _width = width;
            _height = height;
        }

        public static MapProjection Create(
            IReadOnlyList<MapPolygonGeometry> outline,
            IReadOnlyList<EarthquakeMapMarker> markers,
            EarthquakeMapFocusMode focusMode,
            GeoCoordinate? focusedCoordinate,
            double zoomLevel,
            double width,
            double height)
        {
            double centerLongitude = (MinLongitude + MaxLongitude) / 2;
            double centerLatitude = (MinLatitude + MaxLatitude) / 2;
            if (focusedCoordinate is GeoCoordinate location)
            {
                centerLongitude = location.Longitude;
                centerLatitude = location.Latitude;
            }
            else if (focusMode == EarthquakeMapFocusMode.SelectedEvent && markers.Count > 0)
            {
                centerLongitude = markers.Average(item => item.Coordinate.Longitude);
                centerLatitude = markers.Average(item => item.Coordinate.Latitude);
            }

            double scale = Math.Min(
                (width - 48) / (MaxLongitude - MinLongitude),
                (height - 48) / (MaxLatitude - MinLatitude));
            return new MapProjection(
                scale * Math.Max(1, zoomLevel),
                centerLongitude,
                centerLatitude,
                width,
                height);
        }

        public Point Project(GeoCoordinate coordinate)
        {
            return new Point(
                _width / 2 + (coordinate.Longitude - _centerLongitude) * _scale,
                _height / 2 - (coordinate.Latitude - _centerLatitude) * _scale);
        }
    }
}
