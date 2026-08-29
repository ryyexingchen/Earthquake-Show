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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TsunamiPageViewModel.ForecastAreaLevels)
            or nameof(TsunamiPageViewModel.HasMapGeometry)
            or nameof(TsunamiPageViewModel.ObservationStations))
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

        foreach (TsunamiObservationStationDisplay station in viewModel.ObservationStations)
        {
            if (station.Latitude is not double latitude || station.Longitude is not double longitude ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude))
            {
                continue;
            }

            Point point = projection.Project(new GeoCoordinate(latitude, longitude));
            Color color = GetLevelColor(station.Level);
            Ellipse marker = new()
            {
                Width = 11,
                Height = 11,
                Fill = new SolidColorBrush(color),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                ToolTip = station.Name + "（" + station.Code + "）\n" +
                    station.ObservationStatusText + " · " + station.HeightText,
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            MapCanvas.Children.Add(marker);
        }
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
