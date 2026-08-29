using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.Views;

public partial class TsunamiMapView : UserControl
{
    private static readonly Color InactiveCoastColor = Color.FromRgb(103, 135, 145);
    private bool _renderPending;

    public TsunamiMapView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private TsunamiPageViewModel? ViewModel => DataContext as TsunamiPageViewModel;

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

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e) => RequestRender();

    private void OnMapMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Delta > 0)
        {
            ViewModel.ZoomMapIn();
        }
        else if (e.Delta < 0)
        {
            ViewModel.ZoomMapOut();
        }

        e.Handled = true;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ViewModel?.ZoomMapIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ViewModel?.ZoomMapOut();

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => ViewModel?.ResetMapZoom();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TsunamiPageViewModel.ForecastAreaLevels)
            or nameof(TsunamiPageViewModel.HasMapGeometry)
            or nameof(TsunamiPageViewModel.ObservationStations)
            or nameof(TsunamiPageViewModel.SelectedObservationStation)
            or nameof(TsunamiPageViewModel.MapLines)
            or nameof(TsunamiPageViewModel.MapZoomLevel))
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

    private void RenderMap()
    {
        MapCanvas.Children.Clear();
        TsunamiPageViewModel? viewModel = ViewModel;
        if (viewModel is null || !viewModel.HasMapGeometry ||
            MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return;
        }

        MapProjection projection = MapProjection.Create(
            viewModel.MapBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        MapZoomTransform.ScaleX = viewModel.MapZoomLevel;
        MapZoomTransform.ScaleY = viewModel.MapZoomLevel;
        UpdateSelectedStationPan(viewModel, projection);
        foreach (IGrouping<string, TsunamiMapLine> group in viewModel.MapLines.GroupBy(
                     line => line.Code,
                     StringComparer.Ordinal))
        {
            TsunamiLevel level = viewModel.ForecastAreaLevels.TryGetValue(
                group.Key,
                out TsunamiLevel mappedLevel)
                ? mappedLevel
                : TsunamiLevel.Unknown;
            StreamGeometry geometry = new();
            using (StreamGeometryContext context = geometry.Open())
            {
                foreach (TsunamiMapLine line in group)
                {
                    if (line.Coordinates.Length < 2)
                    {
                        continue;
                    }

                    context.BeginFigure(projection.Project(line.Coordinates[0]), false, false);
                    foreach (GeoCoordinate coordinate in line.Coordinates.Skip(1))
                    {
                        context.LineTo(projection.Project(coordinate), true, false);
                    }
                }
            }

            geometry.Freeze();
            Color color = GetLevelColor(level);
            MapCanvas.Children.Add(new Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(level == TsunamiLevel.Unknown
                    ? InactiveCoastColor
                    : color),
                StrokeThickness = level == TsunamiLevel.Unknown ? 0.7 : 3,
                Opacity = level == TsunamiLevel.Unknown ? 0.6 : 0.95,
                SnapsToDevicePixels = true,
            });
        }

        foreach (TsunamiObservationStationDisplay station in viewModel.ObservationStations
                     .Where(item => item.HasMeasuredTsunami))
        {
            if (station.Latitude is not double latitude || station.Longitude is not double longitude ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude))
            {
                continue;
            }

            Point point = projection.Project(new GeoCoordinate(latitude, longitude));
            Color color = GetLevelColor(station.Level);
            bool isSelected = string.Equals(
                station.Code,
                viewModel.SelectedObservationStation?.Code,
                StringComparison.Ordinal);
            Ellipse marker = new()
            {
                Width = isSelected ? 15 : 11,
                Height = isSelected ? 15 : 11,
                Fill = new SolidColorBrush(color),
                Stroke = Brushes.White,
                StrokeThickness = isSelected ? 3 : 1.5,
                ToolTip = station.Name + "（" + station.Code + "）\n" +
                    station.ObservationStatusText + " · " + station.HeightText +
                    (string.IsNullOrWhiteSpace(station.PublicationText)
                        ? string.Empty
                        : "\n" + station.PublicationText),
            };
            marker.MouseLeftButtonUp += (_, _) => viewModel.SelectObservationStation(station.Code);
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            MapCanvas.Children.Add(marker);
        }
    }

    private void UpdateSelectedStationPan(
        TsunamiPageViewModel viewModel,
        MapProjection projection)
    {
        if (!viewModel.TryGetSelectedObservationCoordinate(out GeoCoordinate coordinate))
        {
            if (!viewModel.HasSelectedObservationStation)
            {
                MapPanTransform.X = 0;
                MapPanTransform.Y = 0;
            }

            return;
        }

        Point projected = projection.Project(coordinate);
        Vector offset = GetStationCenteringOffset(
            projected,
            viewModel.MapZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        MapPanTransform.X = offset.X;
        MapPanTransform.Y = offset.Y;
    }

    internal static Vector GetStationCenteringOffset(
        Point projected,
        double zoomLevel,
        double width,
        double height)
    {
        Point center = new(width / 2, height / 2);
        return new(
            center.X - (center.X + (projected.X - center.X) * zoomLevel),
            center.Y - (center.Y + (projected.Y - center.Y) * zoomLevel));
    }

    private static Color GetLevelColor(TsunamiLevel level) => level switch
    {
        TsunamiLevel.MinorChange => Color.FromRgb(44, 137, 196),
        TsunamiLevel.Advisory => Color.FromRgb(221, 171, 31),
        TsunamiLevel.Warning => Color.FromRgb(205, 48, 43),
        TsunamiLevel.MajorWarning => Color.FromRgb(117, 53, 157),
        TsunamiLevel.NoConcern => Color.FromRgb(145, 157, 162),
        _ => InactiveCoastColor,
    };

    private sealed class MapProjection
    {
        private readonly double _scale;
        private readonly MapGeometryBounds _bounds;
        private readonly double _width;
        private readonly double _height;

        private MapProjection(double scale, MapGeometryBounds bounds, double width, double height)
        {
            _scale = scale;
            _bounds = bounds;
            _width = width;
            _height = height;
        }

        public static MapProjection Create(MapGeometryBounds bounds, double width, double height)
        {
            double longitudeScale = Math.Max(0.2, Math.Cos(((bounds.MinLatitude + bounds.MaxLatitude) / 2) * Math.PI / 180));
            double scale = Math.Min(
                (width - 28) / (bounds.LongitudeSpan * longitudeScale),
                (height - 28) / bounds.LatitudeSpan);
            return new(scale, bounds, width, height);
        }

        public Point Project(GeoCoordinate coordinate)
        {
            double longitudeScale = Math.Max(0.2, Math.Cos(((
                _bounds.MinLatitude + _bounds.MaxLatitude) / 2) * Math.PI / 180));
            double centerLongitude = (_bounds.MinLongitude + _bounds.MaxLongitude) / 2;
            double centerLatitude = (_bounds.MinLatitude + _bounds.MaxLatitude) / 2;
            return new(
                _width / 2 + (coordinate.Longitude - centerLongitude) * _scale * longitudeScale,
                _height / 2 - (coordinate.Latitude - centerLatitude) * _scale);
        }
    }
}
