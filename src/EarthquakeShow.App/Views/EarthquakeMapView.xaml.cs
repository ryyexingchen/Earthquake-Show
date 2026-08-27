using System.Collections.Immutable;
using System.Diagnostics;
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
    internal const double StationLabelZoomThreshold = 8;

    private static readonly Color OutlineFill = Color.FromRgb(243, 239, 228);
    private static readonly Color OutlineStroke = Color.FromRgb(121, 143, 153);
    private static readonly FontFamily StationLabelFont = new("Segoe UI");
    private static readonly Dictionary<int, SolidColorBrush> BrushCache = [];
    private bool _renderPending;
    private bool _isPanning;
    private Point _lastPanPoint;
    private Vector _panOffset;
    private Vector _renderedPanOffset;
    private GeoCoordinate? _viewportCenter;
    private GeoCoordinate? _lastFocusedCoordinate;
    private long _lastPanTraceAt;

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
            ViewModel.GeometryChanging += OnGeometryChanging;
            ViewModel.AutoScale(GetAutomaticZoomLevel());
        }

        _panOffset = default;
        _renderedPanOffset = default;
        MapPanTransform.X = 0;
        MapPanTransform.Y = 0;
        _viewportCenter = null;
        _lastFocusedCoordinate = ViewModel?.FocusedCoordinate;
        RenderMap();
        await EnsureMapDetailLevelAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopPanning();
        _panOffset = default;
        _renderedPanOffset = default;
        MapPanTransform.X = 0;
        MapPanTransform.Y = 0;
        _viewportCenter = null;
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.GeometryChanging -= OnGeometryChanging;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel?.FollowSelection == true &&
            ViewModel.HasSelectedEvent &&
            ViewModel.EffectiveFocusMode == EarthquakeMapFocusMode.SelectedEvent)
        {
            ViewModel.AutoScale(GetAutomaticZoomLevel());
        }

        RequestRender();
        _ = EnsureMapDetailLevelAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EarthquakeMapViewModel.FocusedCoordinate) &&
            ShouldResetPanForFocusedCoordinate(_lastFocusedCoordinate, ViewModel?.FocusedCoordinate))
        {
            _lastFocusedCoordinate = ViewModel?.FocusedCoordinate;
            _panOffset = default;
        }

        if (e.PropertyName == nameof(EarthquakeMapViewModel.ViewedReportKey))
        {
            _panOffset = default;
            _viewportCenter = null;
            ViewModel?.AutoScale(GetAutomaticZoomLevel());
            RequestRender();
            _ = EnsureMapDetailLevelAsync();
        }

        if (e.PropertyName == nameof(EarthquakeMapViewModel.SelectedMapSelection) &&
            ViewModel?.SelectedMapSelection is not null &&
            ViewModel.TryGetSelectedObservationView(
                out GeoCoordinate selectedCenter,
                out MapGeometryBounds selectedBounds))
        {
            _viewportCenter = null;
            _panOffset = default;
            ViewModel.FocusSelectedObservation();
            ViewModel.AutoScalePreservingFocus(
                GetAutomaticZoomLevel(selectedBounds, selectedCenter));
        }

        if (ViewModel?.FollowSelection == true &&
            ShouldResetPanForFollowState(e.PropertyName))
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
            or nameof(EarthquakeMapViewModel.HasSelectedEvent)
            or nameof(EarthquakeMapViewModel.SelectedMapSelection))
        {
            RequestRender();
        }

        if (e.PropertyName == nameof(EarthquakeMapViewModel.ZoomLevel))
        {
            _ = EnsureMapDetailLevelAsync();
        }
        else if (e.PropertyName is nameof(EarthquakeMapViewModel.EffectiveFocusMode)
            or nameof(EarthquakeMapViewModel.HasSelectedEvent))
        {
            _ = EnsureMapDetailLevelAsync();
        }
    }

    internal static bool ShouldResetPanForFocusedCoordinate(
        GeoCoordinate? previous,
        GeoCoordinate? current)
    {
        return !Nullable.Equals(previous, current);
    }

    internal static bool ShouldResetPanForFollowState(string? propertyName)
    {
        return propertyName is
            nameof(EarthquakeMapViewModel.FollowSelection) or
            nameof(EarthquakeMapViewModel.EffectiveFocusMode) or
            nameof(EarthquakeMapViewModel.HasSelectedEvent);
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
        ViewModel?.BeginManualInteraction();
        ViewModel?.ZoomIn();
        await EnsureMapDetailLevelAsync();
    }

    private async void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.BeginManualInteraction();
        ViewModel?.ZoomOut();
        await EnsureMapDetailLevelAsync();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        _panOffset = default;
        _viewportCenter = null;
        ViewModel?.ResetView();
    }

    private async void OnFocusSelectedClick(object sender, RoutedEventArgs e)
    {
        _panOffset = default;
        _viewportCenter = null;
        ViewModel?.FocusSelectedEvent();
        ViewModel?.AutoScale(GetAutomaticZoomLevel());
        await EnsureMapDetailLevelAsync();
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
        TraceMap("MouseDown", GetViewportCenter());
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
            if (Environment.TickCount64 - _lastPanTraceAt >= 100)
            {
                _lastPanTraceAt = Environment.TickCount64;
                TraceMap("MouseMove", GetViewportCenter(), $"delta={FormatVector(delta)}");
            }
            ApplyPanTransform();
        }

        e.Handled = true;
    }

    private async void OnMapMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_isPanning)
        {
            return;
        }

        CommitViewportCenter("MouseUp");
        StopPanning();
        await EnsureMapDetailLevelAsync();
        e.Handled = true;
    }

    private void OnMapLostMouseCapture(object sender, MouseEventArgs e)
    {
        bool wasPanning = _isPanning;
        if (wasPanning)
        {
            CommitViewportCenter("LostMouseCapture");
        }
        StopPanning();
        if (wasPanning)
        {
            _ = EnsureMapDetailLevelAsync();
        }
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
            CommitViewportCenter("MouseWheel");
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

        MapGeometryBounds? viewportBounds = ViewModel.IsDistantEvent
            ? null
            : GetDetailViewportBounds();
        GeoCoordinate? viewportCenter = null;
        if (ViewModel.WillChangeDetailLevel(viewportBounds) &&
            GetViewportCenter() is GeoCoordinate currentCenter)
        {
            viewportCenter = currentCenter;
            _viewportCenter = currentCenter;
        }

        TraceMap(
            "EnsureDetail",
            GetViewportCenter(),
            $"willChange={viewportCenter is not null}, bounds={FormatBounds(viewportBounds)}");

        try
        {
            await ViewModel.EnsureDetailLevelForZoomAsync(
                viewportBounds: viewportBounds,
                viewportCenter: viewportCenter);
        }
        catch (ObjectDisposedException)
        {
            // 控件卸载期间忽略异步加载竞态。
        }
    }

    private GeoCoordinate? GetViewportCenter()
    {
        if (MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return null;
        }

        return CreateProjection().Unproject(
            new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2));
    }

    private MapGeometryBounds? GetDetailViewportBounds()
    {
        if (MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return null;
        }

        MapProjection projection = CreateProjection();
        GeoCoordinate topLeft = projection.Unproject(new Point(0, 0));
        GeoCoordinate bottomRight = projection.Unproject(
            new Point(MapCanvas.ActualWidth, MapCanvas.ActualHeight));
        double longitudeSpan = Math.Abs(bottomRight.Longitude - topLeft.Longitude);
        double latitudeSpan = Math.Abs(bottomRight.Latitude - topLeft.Latitude);
        double longitudeMargin = Math.Max(0.1, longitudeSpan * 0.2);
        double latitudeMargin = Math.Max(0.1, latitudeSpan * 0.2);
        return new MapGeometryBounds(
            Math.Min(topLeft.Longitude, bottomRight.Longitude) - longitudeMargin,
            Math.Max(topLeft.Longitude, bottomRight.Longitude) + longitudeMargin,
            Math.Min(topLeft.Latitude, bottomRight.Latitude) - latitudeMargin,
            Math.Max(topLeft.Latitude, bottomRight.Latitude) + latitudeMargin);
    }

    private double GetAutomaticZoomLevel()
    {
        if (ViewModel is null)
        {
            return 1;
        }

        if (ViewModel.IsDistantEvent)
        {
            return EarthquakeMapViewModel.MaxSmallZoomLevel;
        }

        if (
            MapCanvas.ActualWidth < 10 ||
            MapCanvas.ActualHeight < 10 ||
            !ViewModel.TryGetSelectedEventBounds(out MapGeometryBounds eventBounds))
        {
            return 1;
        }

        GeoCoordinate center;
        if (!ViewModel.TryGetSelectedEventFocusCoordinate(out center))
        {
            center = new GeoCoordinate(
                (eventBounds.MinLatitude + eventBounds.MaxLatitude) / 2,
                (eventBounds.MinLongitude + eventBounds.MaxLongitude) / 2);
        }

        MapGeometryBounds centeredEventBounds = EarthquakeMapViewModel.CenterEventBounds(
            eventBounds,
            center);
        double overviewScale = MapProjection.CalculateFitScale(
            ViewModel.OverviewBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        double eventScale = MapProjection.CalculateFitScale(
            centeredEventBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        return MapProjection.ZoomLevelForScale(eventScale / overviewScale);
    }

    private double GetAutomaticZoomLevel(
        MapGeometryBounds selectedBounds,
        GeoCoordinate selectedCenter)
    {
        MapGeometryBounds centeredBounds = EarthquakeMapViewModel.CenterEventBounds(
            selectedBounds,
            selectedCenter);
        double overviewScale = MapProjection.CalculateFitScale(
            ViewModel!.OverviewBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        double selectedScale = MapProjection.CalculateFitScale(
            centeredBounds,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        return MapProjection.ZoomLevelForScale(selectedScale / overviewScale);
    }

    private void OnGeometryChanging(
        object? sender,
        MapGeometryChangingEventArgs e)
    {
        if (ViewModel is null || MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return;
        }

        GeoCoordinate? previousCommittedCenter = _viewportCenter;
        GeoCoordinate projectedCenter = CreateProjection().Unproject(
            new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2));
        // 几何替换前的画布中心代表用户此刻实际看到的位置；异步请求携带的中心可能已经过期。
        _viewportCenter = projectedCenter;
        _panOffset = default;
        TraceMap(
            "GeometryChanging",
            projectedCenter,
            $"previousCommitted={FormatCoordinate(previousCommittedCenter)}, " +
            $"preferred={FormatCoordinate(e.PreferredCenter)}, projected={FormatCoordinate(projectedCenter)}");
    }

    private void CommitViewportCenter(string reason)
    {
        GeoCoordinate? center = GetViewportCenter();
        if (center is not null)
        {
            _viewportCenter = center;
        }

        _panOffset = default;
        TraceMap(reason, center);
        RequestRender();
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

        long renderStarted = Stopwatch.GetTimestamp();
        MapPanTransform.X = 0;
        MapPanTransform.Y = 0;
        _renderedPanOffset = _panOffset;
        MapContentCanvas.Children.Clear();
        UpdateLegend();
        GeoCoordinate? selectedEventFocusCoordinate =
            ViewModel.TryGetSelectedEventFocusCoordinate(out GeoCoordinate eventFocus)
                ? eventFocus
                : null;
        MapGeometryBounds? selectedEventBounds =
            ViewModel.TryGetSelectedEventBounds(out MapGeometryBounds eventBounds)
                ? eventBounds
                : null;
        MapProjection projection = CreateProjection(
            selectedEventFocusCoordinate,
            selectedEventBounds,
            _viewportCenter);
        TraceMap(
            "Render",
            projection.Unproject(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2)),
            $"committed={FormatCoordinate(_viewportCenter)}, focus={FormatCoordinate(ViewModel.FocusedCoordinate)}");
        bool drawBaseOutlineStroke = ViewModel.BoundaryLayers.Count == 0;
        bool fillIntensityAreas = ShouldFillIntensityAreas(ViewModel.ViewedReportType);
        IReadOnlyList<MapPolygonGeometry> visibleOutline = ViewModel.IsDistantEvent
            ? []
            : ViewModel.Outline;

        foreach (MapPolygonGeometry polygon in visibleOutline)
        {
            var shape = new Path
            {
                Data = ToPathGeometry(GetRings(polygon.Rings, polygon.Coordinates), projection),
                Fill = GetBrush(OutlineFill),
                Stroke = drawBaseOutlineStroke
                    ? GetBrush(OutlineStroke)
                    : null,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                ToolTip = polygon.Name,
            };
            MapContentCanvas.Children.Add(shape);
        }

        if (fillIntensityAreas)
        {
            foreach (EarthquakeMapArea area in ViewModel.Areas)
            {
                if (!ShouldDrawIntensityArea(ViewModel.ViewedReportType, area.Intensity))
                {
                    continue;
                }

                var shape = new Path
                {
                    Data = ToPathGeometry(GetRings(area.Rings, area.Coordinates), projection),
                    Fill = GetBrush(GetIntensityColor(area.Intensity, 150)),
                    Stroke = GetBrush(GetIntensityBorderColor(area.Intensity, 235)),
                    StrokeThickness = 1.1,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    ToolTip = $"{area.Name} · 震度 {GetIntensityText(area.Intensity)}",
                };
                MapContentCanvas.Children.Add(shape);
            }
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
                    ? GetBrush(GetIntensityColor(municipality.Intensity, 150))
                    : null,
                Stroke = hasIntensity
                    ? GetBrush(Color.FromArgb(225, 42, 50, 55))
                    : GetBrush(OutlineStroke),
                StrokeThickness = 0.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                ToolTip = hasIntensity
                    ? $"{municipality.Name} · 震度 {GetIntensityText(municipality.Intensity)}"
                    : null,
            };
            MapContentCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapBoundaryLayer layer in ViewModel.BoundaryLayers)
        {
            if (layer.Boundaries.Length == 0 ||
                !ShouldDrawIntensityBoundary(ViewModel.ViewedReportType, layer.Intensity))
            {
                continue;
            }

            var shape = new Path
            {
                Data = ToBoundaryPathGeometry(layer.Boundaries, projection),
                Stroke = GetBrush(fillIntensityAreas
                    ? GetIntensityBorderColor(layer.Intensity, 245)
                    : GetIntensityColor(layer.Intensity, 245)),
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            MapContentCanvas.Children.Add(shape);
        }

        foreach (EarthquakeMapArea area in ViewModel.SelectedAreaHighlights)
        {
            DrawSelectionGlow(
                GetRings(area.Rings, area.Coordinates),
                projection,
                area.Name,
                area.Intensity);
        }

        foreach (EarthquakeMapMunicipality municipality in ViewModel.SelectedMunicipalityHighlights)
        {
            DrawSelectionGlow(
                GetRings(municipality.Rings, municipality.Coordinates),
                projection,
                municipality.Name,
                municipality.Intensity);
        }

        if (ViewModel.SelectedStationHighlight is EarthquakeMapMarker selectedStation)
        {
            DrawSelectedMarkerGlow(selectedStation, projection);
        }

        foreach (EarthquakeMapMarker marker in OrderMarkersForRendering(ViewModel.Markers))
        {
            DrawMarker(marker, projection);
        }

        TraceMap(
            "RenderComplete",
            projection.Unproject(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2)),
            $"elapsed={Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds:0.##}ms " +
            $"outline={visibleOutline.Count} areas={ViewModel.Areas.Count} " +
            $"municipalities={ViewModel.Municipalities.Count} " +
            $"boundaries={ViewModel.BoundaryLayers.Count} markers={ViewModel.Markers.Count}");
    }

    private void UpdateLegend()
    {
        IEnumerable<JmaIntensity> visibleIntensities = ViewModel!.Areas
            .Select(area => area.Intensity)
            .Concat(ViewModel.Municipalities.Select(municipality => municipality.Intensity))
            .Concat(ViewModel.Markers
                .Where(marker => marker.Kind == EarthquakeMapMarkerKind.Station)
                .Select(marker => marker.Intensity));
        IReadOnlyList<JmaIntensity> legendIntensities =
            BuildLegendIntensities(visibleIntensities);
        LegendPanel.Visibility = ViewModel.IsDistantEvent || legendIntensities.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegendItemsPanel.Children.Clear();

        for (int index = 0; index < legendIntensities.Count; index++)
        {
            JmaIntensity intensity = legendIntensities[index];
            var row = new StackPanel
            {
                Margin = index == 0 ? default : new Thickness(0, 2, 0, 0),
                Orientation = Orientation.Horizontal,
            };
            row.Children.Add(new Border
            {
                Width = 14,
                Height = 10,
                Background = GetBrush(GetIntensityColor(intensity, 255)),
                CornerRadius = new CornerRadius(2),
            });
            row.Children.Add(new TextBlock
            {
                Margin = new Thickness(5, 0, 0, 0),
                FontSize = 10,
                Text = GetIntensityLegendText(intensity),
            });
            LegendItemsPanel.Children.Add(row);
        }
    }

    internal static IReadOnlyList<JmaIntensity> BuildLegendIntensities(
        IEnumerable<JmaIntensity> intensities)
    {
        JmaIntensity[] materialized = intensities
            .Where(intensity => intensity is >= JmaIntensity.Unknown and <= JmaIntensity.Seven)
            .ToArray();
        var result = new List<JmaIntensity>();
        if (materialized.Contains(JmaIntensity.Unknown))
        {
            result.Add(JmaIntensity.Unknown);
        }

        JmaIntensity maximum = materialized
            .Where(IsKnownIntensity)
            .DefaultIfEmpty(JmaIntensity.Unknown)
            .Max();
        for (int value = (int)JmaIntensity.One; value <= (int)maximum; value++)
        {
            result.Add((JmaIntensity)value);
        }

        return result;
    }

    internal static string GetIntensityLegendText(JmaIntensity intensity)
    {
        return intensity switch
        {
            JmaIntensity.FiveLower => "5-",
            JmaIntensity.FiveUpper => "5+",
            JmaIntensity.SixLower => "6-",
            JmaIntensity.SixUpper => "6+",
            JmaIntensity.Unknown => "不明",
            _ => intensity.ToCode(),
        };
    }

    internal static bool ShouldFillIntensityAreas(EarthquakeReportType reportType)
    {
        return reportType is EarthquakeReportType.SeismicIntensity or
            EarthquakeReportType.Hypocenter;
    }

    internal static bool ShouldDrawIntensityArea(
        EarthquakeReportType reportType,
        JmaIntensity intensity)
    {
        return ShouldFillIntensityAreas(reportType) && IsKnownIntensity(intensity);
    }

    internal static bool ShouldDrawIntensityBoundary(
        EarthquakeReportType reportType,
        JmaIntensity intensity)
    {
        return !ShouldFillIntensityAreas(reportType) || IsKnownIntensity(intensity);
    }

    private MapProjection CreateProjection(
        GeoCoordinate? selectedEventFocusCoordinate = null,
        MapGeometryBounds? selectedEventBounds = null,
        GeoCoordinate? preferredCenter = null)
    {
        EarthquakeMapViewModel viewModel = ViewModel!;
        if (selectedEventFocusCoordinate is null &&
            viewModel.TryGetSelectedEventFocusCoordinate(out GeoCoordinate eventFocus))
        {
            selectedEventFocusCoordinate = eventFocus;
        }

        if (selectedEventBounds is null &&
            viewModel.TryGetSelectedEventBounds(out MapGeometryBounds eventBounds))
        {
            selectedEventBounds = eventBounds;
        }

        preferredCenter ??= _viewportCenter;

        return MapProjection.Create(
            viewModel.Outline,
            viewModel.Municipalities,
            viewModel.Markers,
            viewModel.EffectiveFocusMode,
            viewModel.FocusedCoordinate,
            selectedEventFocusCoordinate,
            selectedEventBounds,
            preferredCenter,
            viewModel.ZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight,
            _panOffset.X,
            _panOffset.Y,
            globalReferenceBounds: viewModel.OverviewBounds);
    }

    private void TraceMap(
        string action,
        GeoCoordinate? center,
        string? detail = null)
    {
        string message =
            $"[MapDebug] {DateTimeOffset.Now:HH:mm:ss.fff} {action} " +
            $"zoom={ViewModel?.ZoomLevel:0.###} detail={ViewModel?.DetailLevel} " +
            $"center={FormatCoordinate(center)} committed={FormatCoordinate(_viewportCenter)} " +
            $"pan={FormatVector(_panOffset)} panning={_isPanning} follow={ViewModel?.FollowSelection}" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}");
        Console.WriteLine(message);
        Debug.WriteLine(message);
    }

    private void ApplyPanTransform()
    {
        Vector offset = _panOffset - _renderedPanOffset;
        MapPanTransform.X = offset.X;
        MapPanTransform.Y = offset.Y;
    }

    private static string FormatCoordinate(GeoCoordinate? coordinate) =>
        coordinate is GeoCoordinate value
            ? $"{value.Latitude:0.####},{value.Longitude:0.####}"
            : "null";

    private static string FormatVector(Vector vector) =>
        $"{vector.X:0.##},{vector.Y:0.##}";

    private static string FormatBounds(MapGeometryBounds? bounds) =>
        bounds is MapGeometryBounds value
            ? $"[{value.MinLongitude:0.###},{value.MaxLongitude:0.###}]x[{value.MinLatitude:0.###},{value.MaxLatitude:0.###}]"
            : "null";

    private static IReadOnlyList<ImmutableArray<GeoCoordinate>> GetRings(
        IReadOnlyList<ImmutableArray<GeoCoordinate>> rings,
        ImmutableArray<GeoCoordinate> coordinates)
    {
        return rings.Count > 0 ? rings : [coordinates];
    }

    internal static bool IsRenderableRing(IReadOnlyList<GeoCoordinate> ring)
    {
        return ring.Count >= 3 && ring.All(coordinate =>
            double.IsFinite(coordinate.Latitude) &&
            double.IsFinite(coordinate.Longitude));
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
                if (!IsRenderableRing(ring))
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
                if (boundary.Coordinates.Length < 2 ||
                    boundary.Coordinates.Any(coordinate =>
                        !double.IsFinite(coordinate.Latitude) ||
                        !double.IsFinite(coordinate.Longitude)))
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
        bool showStationLabel = marker.Kind == EarthquakeMapMarkerKind.Station &&
            ShouldShowStationLabels(ViewModel!.ZoomLevel) &&
            IsKnownIntensity(marker.Intensity);
        double size = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
            ? 15
            : showStationLabel ? 20 : 8;
        var shape = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? GetBrush(Color.FromRgb(190, 61, 52))
                : GetBrush(GetIntensityColor(marker.Intensity, 245)),
            Stroke = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? GetBrush(Colors.White)
                : GetBrush(GetIntensityBorderColor(marker.Intensity, 245)),
            StrokeThickness = 1.5,
            ToolTip = $"{marker.Label} · 震度 {GetIntensityText(marker.Intensity)}",
        };
        Canvas.SetLeft(shape, point.X - size / 2);
        Canvas.SetTop(shape, point.Y - size / 2);
        MapContentCanvas.Children.Add(shape);

        if (showStationLabel)
        {
            var label = new Label
            {
                Width = size,
                Height = size,
                Content = GetStationMarkerText(marker.Intensity),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = StationLabelFont,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(0),
                Foreground = GetBrush(GetIntensityTextColor(marker.Intensity)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, point.X - size / 2);
            Canvas.SetTop(label, point.Y - size / 2);
            MapContentCanvas.Children.Add(label);
        }
    }

    internal static bool ShouldShowStationLabels(double zoomLevel)
    {
        return double.IsFinite(zoomLevel) && zoomLevel >= StationLabelZoomThreshold;
    }

    internal static string? GetStationMarkerText(JmaIntensity intensity)
    {
        return IsKnownIntensity(intensity) ? GetIntensityLegendText(intensity) : null;
    }

    internal static Color GetIntensityTextColor(JmaIntensity intensity)
    {
        Color fill = GetIntensityColor(intensity, 255);
        double luminance =
            (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255;
        return luminance > 0.58 ? Colors.Black : Colors.White;
    }

    private void DrawSelectionGlow(
        IReadOnlyList<ImmutableArray<GeoCoordinate>> rings,
        MapProjection projection,
        string name,
        JmaIntensity? intensity)
    {
        ImmutableArray<ImmutableArray<GeoCoordinate>> renderableRings = rings
            .Where(ring => IsRenderableRing(ring))
            .ToImmutableArray();
        if (renderableRings.IsDefaultOrEmpty)
        {
            return;
        }

        StreamGeometry geometry = ToPathGeometry(renderableRings, projection);
        (Color outerColor, Color innerColor) = GetSelectionColors(intensity);
        var fill = new Path
        {
            Data = geometry,
            Fill = intensity is JmaIntensity known && IsKnownIntensity(known)
                ? GetBrush(GetIntensityColor(known, 150))
                : null,
            IsHitTestVisible = false,
        };
        MapContentCanvas.Children.Add(fill);

        var outer = new Path
        {
            Data = geometry,
            Fill = null,
            Stroke = GetBrush(outerColor),
            StrokeThickness = 8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            ToolTip = $"已选中：{name}",
        };
        MapContentCanvas.Children.Add(outer);

        var inner = new Path
        {
            Data = geometry,
            Fill = null,
            Stroke = GetBrush(innerColor),
            StrokeThickness = 2.4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        MapContentCanvas.Children.Add(inner);
    }

    private static (Color Outer, Color Inner) GetSelectionColors(JmaIntensity? intensity)
    {
        if (intensity is JmaIntensity known && IsKnownIntensity(known))
        {
            Color baseColor = GetIntensityColor(known, 255);
            double luminance =
                (0.299 * baseColor.R + 0.587 * baseColor.G + 0.114 * baseColor.B) / 255;
            return luminance > 0.58
                ? (Color.FromArgb(225, 16, 34, 48), baseColor)
                : (Color.FromArgb(225, 246, 250, 252), baseColor);
        }

        return (Color.FromArgb(225, 16, 34, 48), Color.FromRgb(0, 206, 255));
    }

    private void DrawSelectedMarkerGlow(
        EarthquakeMapMarker marker,
        MapProjection projection)
    {
        Point point = projection.Project(marker.Coordinate);
        (Color outerColor, Color innerColor) = GetSelectionColors(marker.Intensity);
        var outer = new Ellipse
        {
            Width = 24,
            Height = 24,
            Fill = null,
            Stroke = GetBrush(outerColor),
            StrokeThickness = 5,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outer, point.X - outer.Width / 2);
        Canvas.SetTop(outer, point.Y - outer.Height / 2);
        MapContentCanvas.Children.Add(outer);

        var inner = new Ellipse
        {
            Width = 13,
            Height = 13,
            Fill = null,
            Stroke = GetBrush(innerColor),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(inner, point.X - inner.Width / 2);
        Canvas.SetTop(inner, point.Y - inner.Height / 2);
        MapContentCanvas.Children.Add(inner);
    }

    private static Color GetIntensityColor(JmaIntensity intensity, byte alpha)
    {
        Color color = intensity switch
        {
            JmaIntensity.One => Color.FromRgb(120, 199, 216),
            JmaIntensity.Two => Color.FromRgb(86, 193, 168),
            JmaIntensity.Three => Color.FromRgb(240, 201, 67),
            JmaIntensity.Four => Color.FromRgb(232, 154, 60),
            JmaIntensity.FiveLower => Color.FromRgb(232, 94, 63),
            JmaIntensity.FiveUpper => Color.FromRgb(240, 68, 56),
            JmaIntensity.SixLower => Color.FromRgb(181, 18, 85),
            JmaIntensity.SixUpper => Color.FromRgb(142, 36, 170),
            JmaIntensity.Seven => Color.FromRgb(74, 20, 140),
            _ => Color.FromRgb(230, 235, 239),
        };
        color.A = alpha;
        return color;
    }

    private static SolidColorBrush GetBrush(Color color)
    {
        int key = color.A << 24 | color.R << 16 | color.G << 8 | color.B;
        if (BrushCache.TryGetValue(key, out SolidColorBrush? brush))
        {
            return brush;
        }

        brush = new SolidColorBrush(color);
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    internal static IEnumerable<EarthquakeMapMarker> OrderMarkersForRendering(
        IEnumerable<EarthquakeMapMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        return markers
            .Where(marker => marker.Kind != EarthquakeMapMarkerKind.Hypocenter)
            .Concat(markers.Where(marker => marker.Kind == EarthquakeMapMarkerKind.Hypocenter));
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

    internal sealed class MapProjection
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
            MapGeometryBounds? selectedEventBounds,
            GeoCoordinate? preferredCenter,
            double zoomLevel,
            double width,
            double height,
            double panX,
            double panY,
            MapGeometryBounds? globalReferenceBounds = null)
        {
            bool isEventView = focusMode == EarthquakeMapFocusMode.SelectedEvent &&
                selectedEventBounds is MapGeometryBounds;
            MapGeometryBounds bounds = isEventView
                ? NormalizeEventBounds(selectedEventBounds!.Value)
                : GetBounds(outline, municipalities, markers);
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

            if (preferredCenter is GeoCoordinate preservedCenter)
            {
                centerLongitude = preservedCenter.Longitude;
                centerLatitude = preservedCenter.Latitude;
            }

            if (isEventView)
            {
                bounds = EarthquakeMapViewModel.CenterEventBounds(
                    bounds,
                    new GeoCoordinate(centerLatitude, centerLongitude));
            }

            double longitudeScaleFactor = Math.Max(
                0.2,
                Math.Cos(centerLatitude * Math.PI / 180));
            double scale = CalculateFitScale(
                globalReferenceBounds ?? bounds,
                width,
                height);
            return new MapProjection(
                scale * Math.Pow(
                    1.25,
                    Math.Clamp(
                        zoomLevel,
                        EarthquakeMapViewModel.MaxSmallZoomLevel,
                        EarthquakeMapViewModel.MaxBigZoomLevel) - 1),
                longitudeScaleFactor,
                centerLongitude,
                centerLatitude,
                width,
                height,
                panX,
                panY);
        }

        internal static double CalculateFitScale(
            MapGeometryBounds bounds,
            double width,
            double height)
        {
            double longitudeScaleFactor = Math.Max(
                0.2,
                Math.Cos(((bounds.MinLatitude + bounds.MaxLatitude) / 2) * Math.PI / 180));
            double horizontalPadding = width * 0.06;
            double verticalPadding = height * 0.06;
            return Math.Min(
                (width - horizontalPadding * 2) / (bounds.LongitudeSpan * longitudeScaleFactor),
                (height - verticalPadding * 2) / bounds.LatitudeSpan);
        }

        internal static double ZoomLevelForScale(double scaleRatio)
        {
            if (scaleRatio <= 0 || double.IsNaN(scaleRatio))
            {
                return EarthquakeMapViewModel.MaxSmallZoomLevel;
            }

            double zoomLevel = 1 + Math.Log(scaleRatio, 1.25);
            return Math.Clamp(
                zoomLevel,
                EarthquakeMapViewModel.MaxSmallZoomLevel,
                EarthquakeMapViewModel.MaxBigZoomLevel);
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

        private static MapGeometryBounds NormalizeEventBounds(MapGeometryBounds bounds)
        {
            const double minimumSpan = 0.25;
            double centerLongitude = (bounds.MinLongitude + bounds.MaxLongitude) / 2;
            double centerLatitude = (bounds.MinLatitude + bounds.MaxLatitude) / 2;
            double longitudeHalfSpan = Math.Max(minimumSpan / 2, bounds.LongitudeSpan / 2);
            double latitudeHalfSpan = Math.Max(minimumSpan / 2, bounds.LatitudeSpan / 2);
            return new MapGeometryBounds(
                centerLongitude - longitudeHalfSpan,
                centerLongitude + longitudeHalfSpan,
                centerLatitude - latitudeHalfSpan,
                centerLatitude + latitudeHalfSpan);
        }

        public Point Project(GeoCoordinate coordinate)
        {
            double longitudeDelta = NormalizeLongitude(
                coordinate.Longitude - _centerLongitude);
            return new Point(
                _width / 2 + longitudeDelta * _scale * _longitudeScaleFactor + _panX,
                _height / 2 - (coordinate.Latitude - _centerLatitude) * _scale + _panY);
        }

        public GeoCoordinate Unproject(Point point)
        {
            double latitude = _centerLatitude -
                (point.Y - _height / 2 - _panY) / _scale;
            double longitude = _centerLongitude +
                (point.X - _width / 2 - _panX) /
                (_scale * _longitudeScaleFactor);
            return new GeoCoordinate(
                Math.Clamp(latitude, -90, 90),
                NormalizeLongitude(longitude));
        }

        private static double NormalizeLongitude(double longitude)
        {
            double normalized = longitude % 360;
            if (normalized > 180)
            {
                normalized -= 360;
            }
            else if (normalized < -180)
            {
                normalized += 360;
            }

            return normalized;
        }
    }
}
