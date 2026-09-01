using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.Views;

public partial class TsunamiMapView : UserControl
{
    internal const double LineSimplificationPixels = 0.65;
    internal const double DenseLineSimplificationPixels = 1.0;
    internal const int DenseLinePointThreshold = 10000;
    internal const double ObservationLabelZoomThreshold = 4;

    private static readonly Color InactiveCoastColor = Color.FromRgb(103, 135, 145);
    private readonly DispatcherTimer _renderThrottleTimer;
    private readonly DispatcherTimer _wheelZoomTimer;
    private bool _renderPending;
    private bool _isWheelZooming;
    private bool _isPanning;
    private double _wheelBaseZoomLevel;
    private Point _wheelAnchor;
    private Point _lastPanPoint;
    private Vector _automaticPanOffset;
    private Vector _manualPanOffset;
    private bool _centerSelectedStationPending = true;
    private string? _lastSelectedReportIdentity;
    private string? _lastSelectedObservationStationCode;

    public TsunamiMapView()
    {
        InitializeComponent();
        _renderThrottleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(32),
        };
        _renderThrottleTimer.Tick += OnRenderThrottleTimerTick;
        _wheelZoomTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _wheelZoomTimer.Tick += OnWheelZoomTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private TsunamiPageViewModel? ViewModel => DataContext as TsunamiPageViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _centerSelectedStationPending = true;
        _lastSelectedReportIdentity = ViewModel?.SelectedTimelineIdentity;
        _lastSelectedObservationStationCode = ViewModel?.SelectedObservationStation?.Code;
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RenderMap();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderThrottleTimer.Stop();
        _wheelZoomTimer.Stop();
        _isWheelZooming = false;
        ResetWheelZoomTransform();
        StopPanning();
        _automaticPanOffset = default;
        _manualPanOffset = default;
        _centerSelectedStationPending = true;
        _lastSelectedReportIdentity = null;
        _lastSelectedObservationStationCode = null;
        MapPanTransform.X = 0;
        MapPanTransform.Y = 0;
        _renderPending = false;
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e) => RequestRender();

    private void OnMapMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ViewModel is null ||
            IsMapMarkerSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (_isWheelZooming)
        {
            FinishWheelZoomPreview();
        }

        _isPanning = true;
        _lastPanPoint = e.GetPosition(MapCanvas);
        TraceMap("MouseDown", $"point={FormatPoint(_lastPanPoint)}");
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
            _manualPanOffset += delta;
            _lastPanPoint = currentPoint;
            ApplyPanTransform();
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
        TraceMap("MouseUp", $"pan={FormatVector(_manualPanOffset)}");
        e.Handled = true;
    }

    private void OnMapLostMouseCapture(object sender, MouseEventArgs e) => StopPanning();

    private void OnMapMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10 ||
            !ViewModel.HasMapGeometry)
        {
            return;
        }

        Point inputAnchor = e.GetPosition(MapCanvas);
        double previousZoomLevel = ViewModel.MapZoomLevel;
        bool startedWheelZoom = false;
        if (!_isWheelZooming)
        {
            _isWheelZooming = true;
            _wheelBaseZoomLevel = previousZoomLevel;
            _wheelAnchor = inputAnchor;
            startedWheelZoom = true;
        }

        MapProjection before = MapProjection.Create(
            ViewModel.MapBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight,
            previousZoomLevel,
            MapPanTransform.X,
            MapPanTransform.Y);
        GeoCoordinate anchorCoordinate = before.Unproject(inputAnchor);

        bool zoomChanged = e.Delta > 0
            ? ViewModel.ZoomMapIn()
            : e.Delta < 0 && ViewModel.ZoomMapOut();
        if (!zoomChanged)
        {
            if (startedWheelZoom)
            {
                _isWheelZooming = false;
            }

            e.Handled = true;
            return;
        }

        MapProjection after = MapProjection.Create(
            ViewModel.MapBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight,
            ViewModel.MapZoomLevel,
            MapPanTransform.X,
            MapPanTransform.Y);
        Point projectedAnchor = after.Project(anchorCoordinate);
        Vector panAdjustment = GetWheelPanAdjustment(inputAnchor, projectedAnchor);
        _manualPanOffset += panAdjustment;
        ApplyPanTransform();

        double previewScale = GetWheelPreviewScale(_wheelBaseZoomLevel, ViewModel.MapZoomLevel);
        MapZoomTransform.CenterX = _wheelAnchor.X;
        MapZoomTransform.CenterY = _wheelAnchor.Y;
        MapZoomTransform.ScaleX = previewScale;
        MapZoomTransform.ScaleY = previewScale;
        TraceMap(
            "WheelPreview",
            $"anchor={FormatPoint(_wheelAnchor)} projected={FormatPoint(projectedAnchor)} " +
            $"adjust={FormatVector(panAdjustment)} previewScale={previewScale:0.###} " +
            $"previewCenter={FormatPoint(new Point(MapZoomTransform.CenterX, MapZoomTransform.CenterY))}");
        _wheelZoomTimer.Stop();
        _wheelZoomTimer.Start();

        e.Handled = true;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        FinishWheelZoomPreview();
        ViewModel?.ZoomMapIn();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        FinishWheelZoomPreview();
        ViewModel?.ZoomMapOut();
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        FinishWheelZoomPreview();
        _manualPanOffset = default;
        ApplyPanTransform();
        ViewModel?.ResetMapZoom();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TsunamiPageViewModel.SelectedReport))
        {
            string? currentIdentity = ViewModel?.SelectedTimelineIdentity;
            if (ShouldResetPanForReportIdentity(_lastSelectedReportIdentity, currentIdentity))
            {
                _manualPanOffset = default;
                ApplyPanTransform();
            }

            _lastSelectedReportIdentity = currentIdentity;
        }

        if (e.PropertyName == nameof(TsunamiPageViewModel.SelectedObservationStation))
        {
            string? currentCode = ViewModel?.SelectedObservationStation?.Code;
            if (ShouldResetPanForObservationStation(
                    _lastSelectedObservationStationCode,
                    currentCode))
            {
                _manualPanOffset = default;
                _centerSelectedStationPending = true;
                ApplyPanTransform();
            }

            _lastSelectedObservationStationCode = currentCode;
        }

        if (_isWheelZooming && e.PropertyName is
            nameof(TsunamiPageViewModel.ForecastAreaLevels)
            or nameof(TsunamiPageViewModel.HasMapGeometry)
            or nameof(TsunamiPageViewModel.ObservationStations)
            or nameof(TsunamiPageViewModel.SelectedForecastArea)
            or nameof(TsunamiPageViewModel.SelectedObservationStation)
            or nameof(TsunamiPageViewModel.MapLines)
            or nameof(TsunamiPageViewModel.MapZoomLevel))
        {
            return;
        }

        if (e.PropertyName is nameof(TsunamiPageViewModel.ForecastAreaLevels)
            or nameof(TsunamiPageViewModel.HasMapGeometry)
            or nameof(TsunamiPageViewModel.ObservationStations)
            or nameof(TsunamiPageViewModel.SelectedForecastArea)
            or nameof(TsunamiPageViewModel.SelectedObservationStation)
            or nameof(TsunamiPageViewModel.MapLines)
            or nameof(TsunamiPageViewModel.MapZoomLevel))
        {
            RequestRender();
        }
    }

    private void RequestRender()
    {
        _renderPending = true;
        if (!_renderThrottleTimer.IsEnabled)
        {
            _renderThrottleTimer.Start();
        }
    }

    private void OnRenderThrottleTimerTick(object? sender, EventArgs e)
    {
        _renderThrottleTimer.Stop();
        if (!_renderPending || !IsLoaded)
        {
            _renderPending = false;
            return;
        }

        if (_isWheelZooming)
        {
            return;
        }

        _renderPending = false;
        RenderMap();
    }

    private void OnWheelZoomTimerTick(object? sender, EventArgs e)
    {
        _wheelZoomTimer.Stop();
        if (!_isWheelZooming)
        {
            return;
        }

        FinishWheelZoomPreview();
    }

    private void FinishWheelZoomPreview()
    {
        if (!_isWheelZooming)
        {
            return;
        }

        _wheelZoomTimer.Stop();
        _isWheelZooming = false;
        RequestRender();
    }

    private void ResetWheelZoomTransform()
    {
        MapZoomTransform.CenterX = 0;
        MapZoomTransform.CenterY = 0;
        MapZoomTransform.ScaleX = 1;
        MapZoomTransform.ScaleY = 1;
    }

    internal static double GetWheelPreviewScale(double baseZoomLevel, double currentZoomLevel)
    {
        if (!double.IsFinite(baseZoomLevel) || !double.IsFinite(currentZoomLevel))
        {
            return 1;
        }

        return Math.Pow(1.25, currentZoomLevel - baseZoomLevel);
    }

    internal static Vector GetWheelPanAdjustment(Point anchor, Point projectedAnchor) =>
        anchor - projectedAnchor;

    private void TraceMap(string action, string? detail = null)
    {
        string message =
            $"[TsunamiMap] {DateTimeOffset.Now:HH:mm:ss.fff} {action} " +
            $"zoom={ViewModel?.MapZoomLevel:0.###} " +
            $"manualPan={FormatVector(_manualPanOffset)} " +
            $"automaticPan={FormatVector(_automaticPanOffset)} " +
            $"appliedPan={FormatVector(new Vector(MapPanTransform.X, MapPanTransform.Y))} " +
            $"preview={MapZoomTransform.ScaleX:0.###}@" +
            $"{FormatPoint(new Point(MapZoomTransform.CenterX, MapZoomTransform.CenterY))} " +
            $"selectedStation={ViewModel?.SelectedObservationStation?.Code ?? "null"} " +
            $"report={ViewModel?.SelectedTimelineIdentity ?? "null"}";
        Console.WriteLine(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}");
    }

    private static string FormatPoint(Point point) => $"{point.X:0.###},{point.Y:0.###}";

    private static string FormatVector(Vector vector) => $"{vector.X:0.###},{vector.Y:0.###}";

    private void RenderMap()
    {
        // 保留滚轮预览直到正式绘制开始，避免清理临时变换后出现旧倍率闪回。
        ResetWheelZoomTransform();
        TsunamiPageViewModel? viewModel = ViewModel;
        TraceMap(
            "Render",
            $"detail={viewModel?.MapDetailLevel.ToString() ?? "none"} " +
            $"lines={viewModel?.MapLines.Length ?? 0}");
        UpdateLegend(viewModel);
        if (viewModel is null || !viewModel.HasMapGeometry ||
            MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            MapContentCanvas.Children.Clear();
            return;
        }

        MapProjection projection = MapProjection.Create(
            viewModel.MapBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight,
            viewModel.MapZoomLevel,
            0,
            0);
        MapZoomTransform.ScaleX = 1;
        MapZoomTransform.ScaleY = 1;
        UpdateSelectedStationPan(viewModel, projection);
        ImmutableArray<TsunamiMapLine> mapLines = viewModel.MapLines;
        int pointCount = mapLines.Sum(line => line.Coordinates.Length);
        double minimumPixelDistance = GetLineSimplificationPixels(pointCount);
        var nextLayer = new Canvas
        {
            Width = MapCanvas.ActualWidth,
            Height = MapCanvas.ActualHeight,
            Background = Brushes.Transparent,
        };
        var coastHost = new MapDrawingHost(MapCanvas.ActualWidth, MapCanvas.ActualHeight, context =>
        {
            foreach (IGrouping<string, TsunamiMapLine> group in mapLines.GroupBy(
                         line => line.Code,
                         StringComparer.Ordinal))
            {
                TsunamiLevel level = viewModel.ForecastAreaLevels.TryGetValue(
                    group.Key,
                    out TsunamiLevel mappedLevel)
                    ? mappedLevel
                    : TsunamiLevel.Unknown;
                StreamGeometry geometry = new();
                using (StreamGeometryContext geometryContext = geometry.Open())
                {
                    foreach (TsunamiMapLine line in group)
                    {
                        if (line.Coordinates.Length < 2)
                        {
                            continue;
                        }

                        Point firstPoint = projection.Project(line.Coordinates[0]);
                        geometryContext.BeginFigure(firstPoint, false, false);
                        Point previousPoint = firstPoint;
                        for (int index = 1; index < line.Coordinates.Length; index++)
                        {
                            Point projectedPoint = projection.Project(line.Coordinates[index]);
                            if (IsGeometryJump(previousPoint, projectedPoint))
                            {
                                geometryContext.BeginFigure(projectedPoint, false, false);
                                previousPoint = projectedPoint;
                                continue;
                            }

                            if (ShouldKeepLinePoint(
                                previousPoint,
                                projectedPoint,
                                index == line.Coordinates.Length - 1,
                                minimumPixelDistance))
                            {
                                geometryContext.LineTo(projectedPoint, true, false);
                                previousPoint = projectedPoint;
                            }
                        }
                    }
                }

                geometry.Freeze();
                Color color = GetLevelColor(level);
                bool isSelected = IsForecastAreaSelected(
                    group.Key,
                    viewModel.SelectedForecastAreaCode);
                if (isSelected)
                {
                    var highlightBrush = new SolidColorBrush(Colors.White)
                    {
                        Opacity = 0.9,
                    };
                    highlightBrush.Freeze();
                    var highlightPen = new Pen(highlightBrush, 7)
                    {
                        LineJoin = PenLineJoin.Round,
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                    };
                    highlightPen.Freeze();
                    context.DrawGeometry(null, highlightPen, geometry);
                }

                double strokeThickness = isSelected
                    ? 4.5
                    : level == TsunamiLevel.Unknown ? 0.7 : 3;
                Color strokeColor = level == TsunamiLevel.Unknown
                    ? InactiveCoastColor
                    : color;
                var brush = new SolidColorBrush(strokeColor)
                {
                    Opacity = isSelected || level != TsunamiLevel.Unknown ? 0.95 : 0.6,
                };
                brush.Freeze();
                var pen = new Pen(brush, strokeThickness)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
                context.DrawGeometry(null, pen, geometry);
            }
        });
        nextLayer.Children.Add(coastHost);

        bool showObservationLabels = ShouldShowObservationLabels(viewModel.MapZoomLevel);
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
            double markerSize = GetObservationMarkerSize(
                viewModel.MapZoomLevel,
                isSelected,
                showObservationLabels);
            var marker = new Grid
            {
                Width = markerSize,
                Height = markerSize,
                Tag = "tsunami-marker",
                ToolTip = station.Name + "（" + station.Code + "）\n" +
                    station.ObservationStatusText + " · " + station.HeightText +
                    (string.IsNullOrWhiteSpace(station.PublicationText)
                        ? string.Empty
                        : "\n" + station.PublicationText),
            };
            marker.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush(color),
                Stroke = Brushes.White,
                StrokeThickness = isSelected ? 3 : 1.5,
            });
            if (showObservationLabels)
            {
                marker.Children.Add(new TextBlock
                {
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    Foreground = GetMarkerTextBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = GetObservationMarkerText(station),
                    TextAlignment = TextAlignment.Center,
                });
            }

            marker.MouseLeftButtonUp += (_, _) => viewModel.SelectObservationStation(station.Code);
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            nextLayer.Children.Add(marker);
        }

        // 脱离可视树完成整层构建后一次替换，避免先清空旧轮廓线造成闪烁。
        MapContentCanvas.Children.Clear();
        MapContentCanvas.Children.Add(nextLayer);
    }

    private void UpdateSelectedStationPan(
        TsunamiPageViewModel viewModel,
        MapProjection projection)
    {
        if (!viewModel.TryGetSelectedObservationCoordinate(out GeoCoordinate coordinate))
        {
            _automaticPanOffset = default;
            ApplyPanTransform();
            return;
        }

        if (!_centerSelectedStationPending)
        {
            ApplyPanTransform();
            return;
        }

        Point projected = projection.Project(coordinate);
        Vector offset = GetStationCenteringOffset(
            projected,
            viewModel.MapZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        _automaticPanOffset = offset;
        _centerSelectedStationPending = false;
        ApplyPanTransform();
    }

    private void ApplyPanTransform()
    {
        Vector offset = ComposePanOffset(_automaticPanOffset, _manualPanOffset);
        MapPanTransform.X = offset.X;
        MapPanTransform.Y = offset.Y;
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

    internal static Vector ComposePanOffset(Vector automaticOffset, Vector manualOffset) =>
        automaticOffset + manualOffset;

    internal static bool ShouldResetPanForReportIdentity(
        string? previousIdentity,
        string? currentIdentity) =>
        !string.Equals(previousIdentity, currentIdentity, StringComparison.Ordinal);

    internal static bool ShouldResetPanForObservationStation(
        string? previousCode,
        string? currentCode) =>
        !string.Equals(previousCode, currentCode, StringComparison.Ordinal);

    internal static bool IsForecastAreaSelected(string lineCode, string? selectedAreaCode) =>
        !string.IsNullOrWhiteSpace(selectedAreaCode) &&
        string.Equals(lineCode, selectedAreaCode, StringComparison.Ordinal);

    internal static double GetObservationMarkerSize(
        double zoomLevel,
        bool isSelected,
        bool showLabel)
    {
        double baseSize = showLabel ? 30 : isSelected ? 18 : 12;
        return baseSize;
    }

    internal static bool ShouldShowObservationLabels(double zoomLevel) =>
        double.IsFinite(zoomLevel) && zoomLevel > ObservationLabelZoomThreshold;

    internal static string GetObservationMarkerText(
        TsunamiObservationStationDisplay station) =>
        string.IsNullOrWhiteSpace(station.HeightText)
            ? station.ObservationStatusText
            : station.HeightText;

    private static Brush GetMarkerTextBrush(Color background)
    {
        double luminance =
            (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return luminance > 0.58 ? Brushes.Black : Brushes.White;
    }

    private bool IsMapMarkerSource(DependencyObject? source)
    {
        while (source is not null && source != MapCanvas)
        {
            if (source is FrameworkElement { Tag: "tsunami-marker" })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    internal static Vector GetStationCenteringOffset(
        Point projected,
        double zoomLevel,
        double width,
        double height)
    {
        Point center = new(width / 2, height / 2);
        return center - projected;
    }

    internal static double GetLineSimplificationPixels(int pointCount) =>
        pointCount >= DenseLinePointThreshold
            ? DenseLineSimplificationPixels
            : LineSimplificationPixels;

    internal static bool ShouldKeepLinePoint(
        Point previous,
        Point current,
        bool isLastPoint,
        double minimumPixelDistance)
    {
        if (isLastPoint || !double.IsFinite(minimumPixelDistance) || minimumPixelDistance <= 0)
        {
            return true;
        }

        double deltaX = current.X - previous.X;
        double deltaY = current.Y - previous.Y;
        return deltaX * deltaX + deltaY * deltaY >=
            minimumPixelDistance * minimumPixelDistance;
    }

    internal static bool IsGeometryJump(Point previous, Point current)
    {
        if (!double.IsFinite(previous.X) || !double.IsFinite(previous.Y) ||
            !double.IsFinite(current.X) || !double.IsFinite(current.Y))
        {
            return true;
        }

        const double maxSegmentPixels = 500;
        double deltaX = current.X - previous.X;
        double deltaY = current.Y - previous.Y;
        return deltaX * deltaX + deltaY * deltaY > maxSegmentPixels * maxSegmentPixels;
    }

    private void UpdateLegend(TsunamiPageViewModel? viewModel)
    {
        LegendItemsPanel.Children.Clear();
        if (viewModel is null)
        {
            LegendPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TsunamiLevel[] levels = BuildLegendLevels(
            viewModel.ForecastAreaLevels.Values
                .Concat(viewModel.ObservationStations
                    .Where(station => station.HasMeasuredTsunami)
                    .Select(station => station.Level)));
        LegendPanel.Visibility = levels.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int index = 0; index < levels.Length; index++)
        {
            TsunamiLevel level = levels[index];
            var row = new StackPanel
            {
                Margin = index == 0 ? default : new Thickness(0, 2, 0, 0),
                Orientation = Orientation.Horizontal,
            };
            row.Children.Add(new Border
            {
                Width = 12,
                Height = 10,
                Background = new SolidColorBrush(GetLevelColor(level)),
                CornerRadius = new CornerRadius(2),
            });
            row.Children.Add(new TextBlock
            {
                Margin = new Thickness(5, 0, 0, 0),
                FontSize = 10,
                Text = GetTsunamiLegendText(level),
            });
            LegendItemsPanel.Children.Add(row);
        }
    }

    internal static TsunamiLevel[] BuildLegendLevels(IEnumerable<TsunamiLevel> levels)
    {
        TsunamiLevel maximum = levels
            .Where(level => level is TsunamiLevel.MinorChange or TsunamiLevel.Advisory or
                TsunamiLevel.Warning or TsunamiLevel.MajorWarning)
            .DefaultIfEmpty(TsunamiLevel.Unknown)
            .Max();
        if (maximum == TsunamiLevel.Unknown)
        {
            return [];
        }

        return Enumerable.Range(
                (int)TsunamiLevel.MinorChange,
                (int)maximum - (int)TsunamiLevel.MinorChange + 1)
            .Select(value => (TsunamiLevel)value)
            .ToArray();
    }

    internal static string GetTsunamiLegendText(TsunamiLevel level) => level switch
    {
        TsunamiLevel.MinorChange => "海啸预报",
        TsunamiLevel.Advisory => "海啸注意报",
        TsunamiLevel.Warning => "海啸警报",
        TsunamiLevel.MajorWarning => "大海啸警报",
        _ => string.Empty,
    };

    private static Color GetLevelColor(TsunamiLevel level) => level switch
    {
        TsunamiLevel.MinorChange => Color.FromRgb(44, 137, 196),
        TsunamiLevel.Advisory => Color.FromRgb(221, 171, 31),
        TsunamiLevel.Warning => Color.FromRgb(205, 48, 43),
        TsunamiLevel.MajorWarning => Color.FromRgb(117, 53, 157),
        TsunamiLevel.NoConcern => Color.FromRgb(145, 157, 162),
        _ => InactiveCoastColor,
    };

    private sealed class MapDrawingHost : FrameworkElement
    {
        private readonly DrawingVisual _visual = new();

        public MapDrawingHost(double width, double height, Action<DrawingContext> draw)
        {
            Width = width;
            Height = height;
            IsHitTestVisible = false;
            using DrawingContext context = _visual.RenderOpen();
            draw(context);
            AddVisualChild(_visual);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => index == 0
            ? _visual
            : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private sealed class MapProjection
    {
        private readonly double _scale;
        private readonly double _longitudeScale;
        private readonly MapGeometryBounds _bounds;
        private readonly double _width;
        private readonly double _height;
        private readonly double _panX;
        private readonly double _panY;

        private MapProjection(
            double scale,
            double longitudeScale,
            MapGeometryBounds bounds,
            double width,
            double height,
            double panX,
            double panY)
        {
            _scale = scale;
            _longitudeScale = longitudeScale;
            _bounds = bounds;
            _width = width;
            _height = height;
            _panX = panX;
            _panY = panY;
        }

        public static MapProjection Create(
            MapGeometryBounds bounds,
            double width,
            double height,
            double zoomLevel,
            double panX = 0,
            double panY = 0)
        {
            double longitudeScale = Math.Max(
                0.2,
                Math.Cos(((bounds.MinLatitude + bounds.MaxLatitude) / 2) * Math.PI / 180));
            double fitScale = Math.Min(
                (width - 28) / (bounds.LongitudeSpan * longitudeScale),
                (height - 28) / bounds.LatitudeSpan);
            double zoomScale = Math.Pow(
                1.25,
                Math.Max(0, double.IsFinite(zoomLevel) ? zoomLevel - 1 : 0));
            return new(
                fitScale * zoomScale,
                longitudeScale,
                bounds,
                width,
                height,
                panX,
                panY);
        }

        public Point Project(GeoCoordinate coordinate)
        {
            double centerLongitude = (_bounds.MinLongitude + _bounds.MaxLongitude) / 2;
            double centerLatitude = (_bounds.MinLatitude + _bounds.MaxLatitude) / 2;
            return new(
                _width / 2 + (coordinate.Longitude - centerLongitude) * _scale * _longitudeScale + _panX,
                _height / 2 - (coordinate.Latitude - centerLatitude) * _scale + _panY);
        }

        public GeoCoordinate Unproject(Point point)
        {
            double centerLongitude = (_bounds.MinLongitude + _bounds.MaxLongitude) / 2;
            double centerLatitude = (_bounds.MinLatitude + _bounds.MaxLatitude) / 2;
            double latitude = centerLatitude - (point.Y - _height / 2 - _panY) / _scale;
            double longitude = centerLongitude +
                (point.X - _width / 2 - _panX) / (_scale * _longitudeScale);
            return new(
                Math.Clamp(latitude, -90, 90),
                NormalizeLongitude(longitude));
        }

        private static double NormalizeLongitude(double longitude)
        {
            double normalized = longitude % 360;
            return normalized > 180
                ? normalized - 360
                : normalized < -180
                    ? normalized + 360
                    : normalized;
        }
    }
}
