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
                Stroke = new SolidColorBrush(GetIntensityColor(area.Intensity, 235)),
                StrokeThickness = 1.2,
                ToolTip = $"{area.Name} · 震度 {GetIntensityText(area.Intensity)}",
            };
            MapCanvas.Children.Add(shape);
        }

        DrawCatalogStations(
            ViewModel.Markers.Where(marker =>
                marker.Kind == EarthquakeMapMarkerKind.Station && !marker.IsObserved),
            projection);

        foreach (EarthquakeMapMarker marker in ViewModel.Markers.Where(marker =>
                     marker.Kind == EarthquakeMapMarkerKind.Hypocenter || marker.IsObserved))
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
        double size = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
            ? 15
            : marker.IsObserved
                ? 8
                : 4;
        var shape = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? new SolidColorBrush(Color.FromRgb(190, 61, 52))
                : !marker.IsObserved
                    ? new SolidColorBrush(Color.FromRgb(121, 143, 153))
                    : new SolidColorBrush(GetIntensityColor(marker.Intensity, 245)),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5,
            ToolTip = marker.IsObserved
                ? $"{marker.Label} · 震度 {GetIntensityText(marker.Intensity)}"
                : $"{marker.Label} · JMA 观测点目录",
        };
        Canvas.SetLeft(shape, point.X - size / 2);
        Canvas.SetTop(shape, point.Y - size / 2);
        MapCanvas.Children.Add(shape);
    }

    private void DrawCatalogStations(
        IEnumerable<EarthquakeMapMarker> markers,
        MapProjection projection)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            foreach (EarthquakeMapMarker marker in markers)
            {
                Point point = projection.Project(marker.Coordinate);
                const double radius = 1.5;
                context.BeginFigure(
                    new Point(point.X - radius, point.Y - radius),
                    true,
                    true);
                context.LineTo(new Point(point.X + radius, point.Y - radius), true, false);
                context.LineTo(new Point(point.X + radius, point.Y + radius), true, false);
                context.LineTo(new Point(point.X - radius, point.Y + radius), true, false);
            }
        }

        geometry.Freeze();
        MapCanvas.Children.Add(new Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Color.FromRgb(121, 143, 153)),
            IsHitTestVisible = false,
        });
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
            IReadOnlyList<EarthquakeMapMarker> markers,
            EarthquakeMapFocusMode focusMode,
            GeoCoordinate? focusedCoordinate,
            double zoomLevel,
            double width,
            double height)
        {
            MapGeometryBounds bounds = GetBounds(outline, markers);
            double centerLongitude = (bounds.MinLongitude + bounds.MaxLongitude) / 2;
            double centerLatitude = (bounds.MinLatitude + bounds.MaxLatitude) / 2;
            if (focusedCoordinate is GeoCoordinate location)
            {
                centerLongitude = location.Longitude;
                centerLatitude = location.Latitude;
            }
            else if (focusMode == EarthquakeMapFocusMode.SelectedEvent)
            {
                EarthquakeMapMarker[] eventMarkers = markers
                    .Where(item => item.Kind == EarthquakeMapMarkerKind.Hypocenter || item.IsObserved)
                    .ToArray();
                if (eventMarkers.Length > 0)
                {
                    centerLongitude = eventMarkers.Average(item => item.Coordinate.Longitude);
                    centerLatitude = eventMarkers.Average(item => item.Coordinate.Latitude);
                }
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
            IReadOnlyList<EarthquakeMapMarker> markers)
        {
            IEnumerable<GeoCoordinate> coordinates = outline
                .SelectMany(item => item.Rings.IsDefaultOrEmpty
                    ? [item.Coordinates]
                    : item.Rings)
                .SelectMany(item => item)
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
