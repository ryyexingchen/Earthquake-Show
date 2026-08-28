using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.Views;

public partial class EarthquakeMapView : UserControl
{
    internal const double StationLabelZoomThreshold = 8;
    internal const double HighDetailRenderBufferRatio = 0.25;
    internal const double HighDetailBoundarySimplificationPixels = 0.65;
    internal const double DenseHighDetailBoundarySimplificationPixels = 1.0;
    internal const int DenseHighDetailBoundaryThreshold = 8000;

    private static readonly Color OutlineFill = Color.FromRgb(243, 239, 228);
    private static readonly Color OutlineStroke = Color.FromRgb(121, 143, 153);
    private static readonly FontFamily StationLabelFont = new("BIZ UDPGothic");
    private static readonly Typeface StationLabelTypeface =
        new(StationLabelFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Dictionary<int, SolidColorBrush> BrushCache = [];
    private static readonly Dictionary<(int Color, double Thickness), Pen> PenCache = [];
    private static readonly Dictionary<(JmaIntensity Intensity, double PixelsPerDip), FormattedText>
        StationLabelTextCache = [];
    private readonly DispatcherTimer _renderThrottleTimer;
    private readonly DispatcherTimer _wheelZoomTimer;
    private readonly Dictionary<MarkerDrawingKey, DrawingGroup> _markerDrawingCache = [];
    private MapDrawingHost? _staticGeometryHost;
    private StaticGeometryCacheKey? _staticGeometryCacheKey;
    private MapGeometryBounds? _renderedHighDetailBounds;
    private string? _markerCacheReportKey;
    private bool _markerCacheShowLabels;
    private double _markerCachePixelsPerDip;
    private bool _renderPending;
    private bool _panContentChanged;
    private bool _isPanning;
    private bool _isWheelZooming;
    private double _wheelBaseZoomLevel;
    private Point _wheelAnchor;
    private Point _lastPanPoint;
    private Vector _panOffset;
    private Vector _renderedPanOffset;
    private GeoCoordinate? _viewportCenter;
    private GeoCoordinate? _lastFocusedCoordinate;
    private long _lastPanTraceAt;
    private Task? _detailEnsureTask;
    private bool _detailEnsureDispatchPending;
    private bool _detailEnsureRequested;
    private bool _detailEnsureDeferredLogged;
    private bool _renderDeferredForDetailDecrease;

    public EarthquakeMapView()
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
        _renderedHighDetailBounds = null;
        _lastFocusedCoordinate = ViewModel?.FocusedCoordinate;
        MapContentCanvas.CacheMode = CreatePanCache();
        _renderThrottleTimer.Stop();
        _renderPending = false;
        _panContentChanged = false;
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
        _isWheelZooming = false;
        ResetWheelZoomTransform();
        _wheelZoomTimer.Stop();
        _detailEnsureRequested = false;
        _detailEnsureDispatchPending = false;
        _renderDeferredForDetailDecrease = false;
        _markerDrawingCache.Clear();
        _markerCacheReportKey = null;
        _staticGeometryHost = null;
        _staticGeometryCacheKey = null;
        _renderedHighDetailBounds = null;
        MapContentCanvas.CacheMode = null;
        _renderThrottleTimer.Stop();
        _renderPending = false;
        _panContentChanged = false;
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.GeometryChanging -= OnGeometryChanging;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isPanning)
        {
            _panContentChanged = true;
        }

        if (!_isPanning &&
            ViewModel?.FollowSelection == true &&
            ViewModel.HasSelectedEvent &&
            ViewModel.EffectiveFocusMode == EarthquakeMapFocusMode.SelectedEvent)
        {
            ViewModel.AutoScale(GetAutomaticZoomLevel());
        }

        RequestRender();
        QueueDetailLevelCheck();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_isPanning &&
            e.PropertyName == nameof(EarthquakeMapViewModel.FocusedCoordinate) &&
            ShouldResetPanForFocusedCoordinate(_lastFocusedCoordinate, ViewModel?.FocusedCoordinate))
        {
            _lastFocusedCoordinate = ViewModel?.FocusedCoordinate;
            _panOffset = default;
        }

        if (e.PropertyName == nameof(EarthquakeMapViewModel.ViewedReportKey))
        {
            _panContentChanged |= _isPanning;
            CancelWheelZoomPreview();
            if (!_isPanning)
            {
                _panOffset = default;
                _viewportCenter = null;
                ViewModel?.AutoScale(GetAutomaticZoomLevel());
            }
            else
            {
                TraceMap("AutoScaleSkipped", GetViewportCenter(), "reason=manual-pan-report-update");
            }

            RequestRender();
            QueueDetailLevelCheck();
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
            or nameof(EarthquakeMapViewModel.HasSelectedEvent)
            or nameof(EarthquakeMapViewModel.SelectedMapSelection))
        {
            _panContentChanged |= _isPanning;
            RequestRender();
        }

        if (e.PropertyName == nameof(EarthquakeMapViewModel.ZoomLevel) &&
            !_isWheelZooming)
        {
            QueueDetailLevelCheck();
        }
        else if (e.PropertyName is nameof(EarthquakeMapViewModel.EffectiveFocusMode)
            or nameof(EarthquakeMapViewModel.HasSelectedEvent))
        {
            QueueDetailLevelCheck();
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
        _renderPending = true;
        if (_renderDeferredForDetailDecrease ||
            ShouldDeferRenderDuringInteraction(_isPanning, _isWheelZooming))
        {
            return;
        }

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

        if (ShouldDeferRenderDuringInteraction(_isPanning, _isWheelZooming))
        {
            return;
        }

        _renderPending = false;
        RenderMap();
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
        CancelWheelZoomPreview();
        _panOffset = default;
        _viewportCenter = null;
        ViewModel?.ResetView();
    }

    private async void OnFocusSelectedClick(object sender, RoutedEventArgs e)
    {
        CancelWheelZoomPreview();
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

        if (_isWheelZooming)
        {
            CommitWheelZoom();
            QueueDetailLevelCheck();
        }

        _isPanning = true;
        _panContentChanged = _renderPending;
        _detailEnsureDeferredLogged = false;
        ViewModel.SetMapPanning(true);
        ViewModel.BeginManualInteraction();
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
            if (Environment.TickCount64 - _lastPanTraceAt >= 250)
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

        bool reusePanVisual = ShouldReusePanVisualAfterCommit();
        CommitViewportCenter("MouseUp", requestRender: !reusePanVisual);
        if (reusePanVisual)
        {
            _renderPending = false;
            _renderThrottleTimer.Stop();
            TraceMap("PanVisualReuse", GetViewportCenter(), "reason=mouse-up");
        }
        StopPanning();
        await EnsureMapDetailLevelAsync();
        e.Handled = true;
    }

    private void OnMapLostMouseCapture(object sender, MouseEventArgs e)
    {
        bool wasPanning = _isPanning;
        if (wasPanning)
        {
            bool reusePanVisual = ShouldReusePanVisualAfterCommit();
            CommitViewportCenter("LostMouseCapture", requestRender: !reusePanVisual);
            if (reusePanVisual)
            {
                _renderPending = false;
                _renderThrottleTimer.Stop();
                TraceMap("PanVisualReuse", GetViewportCenter(), "reason=lost-capture");
            }
        }
        StopPanning();
        if (wasPanning)
        {
            _ = EnsureMapDetailLevelAsync();
        }
    }

    private void OnMapMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel is null || MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return;
        }

        Point inputAnchor = e.GetPosition(MapCanvas);
        ViewModel.BeginManualInteraction();
        double previousZoom = ViewModel.ZoomLevel;
        if (!_isWheelZooming)
        {
            _isWheelZooming = true;
            _wheelBaseZoomLevel = previousZoom;
            _wheelAnchor = inputAnchor;
        }

        Point anchor = _wheelAnchor;
        MapProjection before = CreateProjection();
        GeoCoordinate anchorCoordinate = before.Unproject(anchor);
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
            MapZoomTransform.CenterX = _wheelAnchor.X;
            MapZoomTransform.CenterY = _wheelAnchor.Y;
            double previewScale = GetWheelPreviewScale(
                _wheelBaseZoomLevel,
                ViewModel.ZoomLevel);
            MapZoomTransform.ScaleX = previewScale;
            MapZoomTransform.ScaleY = previewScale;
            TraceMap(
                "WheelPreview",
                GetViewportCenter(),
                $"anchor={FormatVector(new Vector(_wheelAnchor.X, _wheelAnchor.Y))} scale={previewScale:0.###}");
        }

        _wheelZoomTimer.Stop();
        _wheelZoomTimer.Start();

        e.Handled = true;
    }

    private async void OnWheelZoomTimerTick(object? sender, EventArgs e)
    {
        _wheelZoomTimer.Stop();
        if (!_isWheelZooming)
        {
            return;
        }

        CommitWheelZoom();
        await EnsureMapDetailLevelAsync();
    }

    private void CommitWheelZoom()
    {
        if (!_isWheelZooming)
        {
            return;
        }

        bool deferRender = ViewModel?.WillDecreaseDetailLevel() == true;
        _isWheelZooming = false;
        if (deferRender)
        {
            _renderDeferredForDetailDecrease = true;
            _renderThrottleTimer.Stop();
        }

        CommitViewportCenter(
            "MouseWheel",
            requestRender: !deferRender,
            preserveVisual: false);
        TraceMap(
            "WheelCommit",
            GetViewportCenter(),
            $"renderDeferred={deferRender}");
    }

    internal static double GetWheelPreviewScale(
        double baseZoomLevel,
        double currentZoomLevel)
    {
        if (!double.IsFinite(baseZoomLevel) || !double.IsFinite(currentZoomLevel))
        {
            return 1;
        }

        return Math.Pow(1.25, currentZoomLevel - baseZoomLevel);
    }

    private Task EnsureMapDetailLevelAsync()
    {
        if (ViewModel is null || !IsLoaded)
        {
            return Task.CompletedTask;
        }

        _detailEnsureRequested = true;
        if (ShouldDeferDetailCheckDuringPan(_isPanning))
        {
            if (!_detailEnsureDeferredLogged)
            {
                _detailEnsureDeferredLogged = true;
                TraceMap("EnsureDetailDeferred", GetViewportCenter(), "reason=manual-pan");
            }
            return Task.CompletedTask;
        }

        _detailEnsureDeferredLogged = false;

        if (_detailEnsureTask is { IsCompleted: false })
        {
            return _detailEnsureTask;
        }

        _detailEnsureTask = ProcessDetailChecksAsync();
        return _detailEnsureTask;
    }

    private void QueueDetailLevelCheck()
    {
        if (!ShouldQueueDetailLevelCheck(_detailEnsureDispatchPending))
        {
            return;
        }

        _detailEnsureDispatchPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _detailEnsureDispatchPending = false;
                if (IsLoaded)
                {
                    _ = EnsureMapDetailLevelAsync();
                }
            }));
    }

    internal static bool ShouldQueueDetailLevelCheck(bool dispatchPending) => !dispatchPending;

    internal static double GetBoundarySimplificationPixels(
        MapDetailLevel detailLevel,
        int visibleBoundaryCount)
    {
        if (detailLevel != MapDetailLevel.High)
        {
            return 0;
        }

        return visibleBoundaryCount >= DenseHighDetailBoundaryThreshold
            ? DenseHighDetailBoundarySimplificationPixels
            : HighDetailBoundarySimplificationPixels;
    }

    private async Task ProcessDetailChecksAsync()
    {
        try
        {
            while (_detailEnsureRequested && IsLoaded)
            {
                if (ShouldDeferDetailCheckDuringPan(_isPanning))
                {
                    return;
                }

                _detailEnsureRequested = false;
                await EnsureMapDetailLevelCoreAsync();
            }
        }
        finally
        {
            if (_renderDeferredForDetailDecrease && !_isPanning)
            {
                _renderDeferredForDetailDecrease = false;
                TraceMap("WheelRenderResumed", GetViewportCenter(), "reason=detail-check-complete");
                RequestRender();
            }
        }
    }

    private async Task EnsureMapDetailLevelCoreAsync()
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
        catch (OperationCanceledException)
        {
            // 视图事件中的过期 LOD 请求不应成为未观察异常。
        }
    }

    internal static bool ShouldDeferDetailCheckDuringPan(bool isPanning) => isPanning;

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
        MapGeometryBounds viewportBounds = GetViewportBounds(
            projection,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        double longitudeSpan = viewportBounds.LongitudeSpan;
        double latitudeSpan = viewportBounds.LatitudeSpan;
        double longitudeMargin = Math.Max(0.1, longitudeSpan * 0.2);
        double latitudeMargin = Math.Max(0.1, latitudeSpan * 0.2);
        return new MapGeometryBounds(
            viewportBounds.MinLongitude - longitudeMargin,
            viewportBounds.MaxLongitude + longitudeMargin,
            viewportBounds.MinLatitude - latitudeMargin,
            viewportBounds.MaxLatitude + latitudeMargin);
    }

    private static MapGeometryBounds GetBufferedRenderBounds(
        MapProjection projection,
        double width,
        double height)
    {
        return ExpandRenderBounds(
            GetViewportBounds(projection, width, height),
            HighDetailRenderBufferRatio);
    }

    private static MapGeometryBounds GetViewportBounds(
        MapProjection projection,
        double width,
        double height)
    {
        GeoCoordinate topLeft = projection.Unproject(new Point(0, 0));
        GeoCoordinate bottomRight = projection.Unproject(new Point(width, height));
        return new MapGeometryBounds(
            Math.Min(topLeft.Longitude, bottomRight.Longitude),
            Math.Max(topLeft.Longitude, bottomRight.Longitude),
            Math.Min(topLeft.Latitude, bottomRight.Latitude),
            Math.Max(topLeft.Latitude, bottomRight.Latitude));
    }

    internal static MapGeometryBounds ExpandRenderBounds(
        MapGeometryBounds bounds,
        double bufferRatio)
    {
        if (!double.IsFinite(bufferRatio) || bufferRatio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferRatio));
        }

        double longitudeMargin = bounds.LongitudeSpan * bufferRatio;
        double latitudeMargin = bounds.LatitudeSpan * bufferRatio;
        return new MapGeometryBounds(
            bounds.MinLongitude - longitudeMargin,
            bounds.MaxLongitude + longitudeMargin,
            bounds.MinLatitude - latitudeMargin,
            bounds.MaxLatitude + latitudeMargin);
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
        // 非拖动时以几何替换前画布实际中心为准，避免异步请求快照覆盖当前视口。
        GeoCoordinate preservedCenter = SelectGeometryCenter(
            _viewportCenter,
            e.PreferredCenter,
            projectedCenter,
            _isPanning);
        _viewportCenter = preservedCenter;
        if (!_isPanning)
        {
            _panOffset = default;
        }
        TraceMap(
            "GeometryChanging",
            preservedCenter,
            $"previousCommitted={FormatCoordinate(previousCommittedCenter)}, " +
            $"preferred={FormatCoordinate(e.PreferredCenter)}, projected={FormatCoordinate(projectedCenter)}, " +
            $"centerSource={(_isPanning ? "committed-or-preferred" : "projected")}");
    }

    internal static GeoCoordinate SelectGeometryCenter(
        GeoCoordinate? committedCenter,
        GeoCoordinate? preferredCenter,
        GeoCoordinate projectedCenter,
        bool isPanning = false)
    {
        return isPanning
            ? committedCenter ?? preferredCenter ?? projectedCenter
            : projectedCenter;
    }

    private void CommitViewportCenter(
        string reason,
        bool requestRender = true,
        bool preserveVisual = true)
    {
        GeoCoordinate? center = GetViewportCenter();
        if (center is not null)
        {
            _viewportCenter = center;
        }

        if (!requestRender && preserveVisual)
        {
            Vector committedVisualOffset = _panOffset - _renderedPanOffset;
            _panOffset = default;
            _renderedPanOffset = -committedVisualOffset;
            MapPanTransform.X = committedVisualOffset.X;
            MapPanTransform.Y = committedVisualOffset.Y;
        }
        else
        {
            _panOffset = default;
        }

        TraceMap(reason, center);
        if (requestRender)
        {
            RequestRender();
        }
    }

    private bool ShouldReusePanVisualAfterCommit()
    {
        if (ViewModel is null || _panContentChanged)
        {
            return false;
        }

        bool renderedCoverageContainsViewport = IsRenderedCoverageCurrent();
        if (!renderedCoverageContainsViewport)
        {
            TraceMap("PanRenderCoverageExpired", GetViewportCenter());
        }

        return ShouldReusePanVisual(
            _isPanning,
            _panContentChanged,
            ViewModel.WillSwitchDetailLevel(),
            renderedCoverageContainsViewport);
    }

    private bool IsRenderedCoverageCurrent()
    {
        if (ViewModel?.DetailLevel != MapDetailLevel.High)
        {
            return true;
        }

        if (_renderedHighDetailBounds is not MapGeometryBounds renderedBounds ||
            MapCanvas.ActualWidth < 10 ||
            MapCanvas.ActualHeight < 10)
        {
            return false;
        }

        MapGeometryBounds viewportBounds = GetViewportBounds(
            CreateProjection(),
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight);
        return renderedBounds.Contains(viewportBounds);
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        ViewModel?.SetMapPanning(false);
        if (MapCanvas.IsMouseCaptured)
        {
            MapCanvas.ReleaseMouseCapture();
        }

        if (!ShouldKeepPanCacheAfterInteraction(IsLoaded))
        {
            MapContentCanvas.CacheMode = null;
        }

        Cursor = null;

        if (_renderPending && IsLoaded && !_renderThrottleTimer.IsEnabled)
        {
            _renderThrottleTimer.Start();
        }
    }

    internal static bool ShouldDeferRenderDuringPan(bool isPanning) => isPanning;

    internal static bool ShouldReusePanVisual(
        bool isPanning,
        bool contentChanged,
        bool detailWillChange,
        bool renderedCoverageContainsViewport)
    {
        return isPanning &&
            !contentChanged &&
            !detailWillChange &&
            renderedCoverageContainsViewport;
    }

    internal static bool ShouldDeferRenderDuringInteraction(
        bool isPanning,
        bool isWheelZooming) => isPanning || isWheelZooming;

    internal static bool HasStaticGeometry(
        int outlineCount,
        int areaCount,
        int municipalityCount,
        int boundaryCount,
        int selectedAreaCount,
        int selectedMunicipalityCount)
    {
        return outlineCount > 0 ||
            areaCount > 0 ||
            municipalityCount > 0 ||
            boundaryCount > 0 ||
            selectedAreaCount > 0 ||
            selectedMunicipalityCount > 0;
    }

    internal static bool HasBaseStaticGeometry(
        int outlineCount,
        int areaCount,
        int municipalityCount,
        int boundaryCount)
    {
        return outlineCount > 0 ||
            areaCount > 0 ||
            municipalityCount > 0 ||
            boundaryCount > 0;
    }

    internal static bool ShouldKeepPanCacheAfterInteraction(bool isLoaded) => isLoaded;

    private static BitmapCache CreatePanCache() => new() { RenderAtScale = 1.0 };

    private void CancelWheelZoomPreview()
    {
        if (!_isWheelZooming)
        {
            return;
        }

        _wheelZoomTimer.Stop();
        _isWheelZooming = false;
        ResetWheelZoomTransform();
    }

    private void ResetWheelZoomTransform()
    {
        MapZoomTransform.CenterX = 0;
        MapZoomTransform.CenterY = 0;
        MapZoomTransform.ScaleX = 1;
        MapZoomTransform.ScaleY = 1;
    }

    private void RenderMap()
    {
        if (!IsLoaded || ViewModel is null || MapCanvas.ActualWidth < 10 || MapCanvas.ActualHeight < 10)
        {
            return;
        }

        EarthquakeMapViewModel mapViewModel = ViewModel;
        long renderStarted = Stopwatch.GetTimestamp();
        ResetWheelZoomTransform();
        MapPanTransform.X = 0;
        MapPanTransform.Y = 0;
        _renderedPanOffset = _panOffset;
        MapContentCanvas.Children.Clear();
        _panContentChanged = false;
        long elementBuildStarted = Stopwatch.GetTimestamp();
        long legendStarted = Stopwatch.GetTimestamp();
        UpdateLegend();
        double legendElapsed = Stopwatch.GetElapsedTime(legendStarted).TotalMilliseconds;
        GeoCoordinate? selectedEventFocusCoordinate =
            mapViewModel.TryGetSelectedEventFocusCoordinate(out GeoCoordinate eventFocus)
                ? eventFocus
                : null;
        MapGeometryBounds? selectedEventBounds =
            mapViewModel.TryGetSelectedEventBounds(out MapGeometryBounds eventBounds)
                ? eventBounds
                : null;
        MapProjection projection = CreateProjection(
            selectedEventFocusCoordinate,
            selectedEventBounds,
            _viewportCenter);
        MapGeometryBounds? renderBounds = mapViewModel.DetailLevel == MapDetailLevel.High
            ? GetBufferedRenderBounds(
                projection,
                MapCanvas.ActualWidth,
                MapCanvas.ActualHeight)
            : null;
        TraceMap(
            "Render",
            projection.Unproject(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2)),
            $"committed={FormatCoordinate(_viewportCenter)}, focus={FormatCoordinate(mapViewModel.FocusedCoordinate)}");
        bool drawBaseOutlineStroke = mapViewModel.BoundaryLayers.Count == 0;
        bool fillIntensityAreas = ShouldFillIntensityAreas(mapViewModel.ViewedReportType);
        IReadOnlyList<MapPolygonGeometry> visibleOutline = mapViewModel.IsDistantEvent
            ? []
            : renderBounds is MapGeometryBounds outlineBounds
                ? mapViewModel.Outline
                    .Where(item => item.Bounds.Intersects(outlineBounds))
                    .ToArray()
                : mapViewModel.Outline;
        IReadOnlyList<EarthquakeMapArea> visibleAreas = renderBounds is MapGeometryBounds areaBounds
            ? mapViewModel.Areas
                .Where(item => item.Bounds.Intersects(areaBounds))
                .ToArray()
            : mapViewModel.Areas;
        IReadOnlyList<EarthquakeMapMunicipality> visibleMunicipalities =
            renderBounds is MapGeometryBounds municipalityBounds
                ? mapViewModel.Municipalities
                    .Where(item => item.Bounds.Intersects(municipalityBounds))
                    .ToArray()
                : mapViewModel.Municipalities;
        IReadOnlyList<EarthquakeMapBoundaryLayer> visibleBoundaryLayers =
            renderBounds is MapGeometryBounds boundaryBounds
                ? mapViewModel.BoundaryLayers
                    .Select(layer => new EarthquakeMapBoundaryLayer(
                        layer.Intensity,
                        layer.Boundaries
                            .Where(item => item.Bounds.Intersects(boundaryBounds))
                            .ToImmutableArray()))
                    .Where(layer => !layer.Boundaries.IsDefaultOrEmpty)
                    .ToArray()
                : mapViewModel.BoundaryLayers;
        IReadOnlyList<EarthquakeMapArea> visibleSelectedAreas =
            renderBounds is MapGeometryBounds selectedAreaBounds
                ? mapViewModel.SelectedAreaHighlights
                    .Where(item => item.Bounds.Intersects(selectedAreaBounds))
                    .ToArray()
                : mapViewModel.SelectedAreaHighlights;
        IReadOnlyList<EarthquakeMapMunicipality> visibleSelectedMunicipalities =
            renderBounds is MapGeometryBounds selectedMunicipalityBounds
                ? mapViewModel.SelectedMunicipalityHighlights
                    .Where(item => item.Bounds.Intersects(selectedMunicipalityBounds))
                    .ToArray()
                : mapViewModel.SelectedMunicipalityHighlights;
        int visibleBoundaryCount = visibleBoundaryLayers.Sum(layer => layer.Boundaries.Length);
        double boundarySimplificationPixels = GetBoundarySimplificationPixels(
            mapViewModel.DetailLevel,
            visibleBoundaryCount);

        StaticGeometryCacheKey staticGeometryCacheKey = CreateStaticGeometryCacheKey(
            mapViewModel,
            selectedEventFocusCoordinate,
            selectedEventBounds);
        bool staticGeometryCacheHit = _staticGeometryHost is not null &&
            ShouldReuseStaticGeometry(
                _staticGeometryCacheKey is StaticGeometryCacheKey cachedKey &&
                AreEquivalentStaticGeometryKeys(cachedKey, staticGeometryCacheKey),
                _staticGeometryHost is not null);
        if (!staticGeometryCacheHit &&
            _staticGeometryCacheKey is StaticGeometryCacheKey previousStaticKey)
        {
            TraceMap(
                "StaticGeometryCacheMiss",
                projection.Unproject(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2)),
                DescribeStaticGeometryCacheDifference(previousStaticKey, staticGeometryCacheKey));
        }
        long staticStarted = Stopwatch.GetTimestamp();
        MapDrawingHost staticGeometryHost;
        if (staticGeometryCacheHit)
        {
            staticGeometryHost = _staticGeometryHost!;
            TraceMap("StaticGeometryReuse", projection.Unproject(new Point(
                MapCanvas.ActualWidth / 2,
                MapCanvas.ActualHeight / 2)));
        }
        else
        {
            // 将静态区域几何合并到一个 DrawingVisual，避免为每个多边形创建 FrameworkElement。
            staticGeometryHost = new MapDrawingHost(
                MapCanvas.ActualWidth,
                MapCanvas.ActualHeight,
                context =>
                {
                foreach (MapPolygonGeometry polygon in visibleOutline)
                {
                    DrawGeometry(
                        context,
                        ToPathGeometry(GetRings(polygon.Rings, polygon.Coordinates), projection),
                        GetBrush(OutlineFill),
                        drawBaseOutlineStroke ? CreatePen(OutlineStroke, 1) : null);
                }

                if (fillIntensityAreas)
                {
                    foreach (EarthquakeMapArea area in visibleAreas)
                    {
                        if (!ShouldDrawIntensityArea(mapViewModel.ViewedReportType, area.Intensity))
                        {
                            continue;
                        }

                        DrawGeometry(
                            context,
                            ToPathGeometry(GetRings(area.Rings, area.Coordinates), projection),
                            GetBrush(GetIntensityColor(area.Intensity, 150)),
                            CreatePen(GetIntensityBorderColor(area.Intensity, 235), 1.1));
                    }
                }

                foreach (EarthquakeMapMunicipality municipality in visibleMunicipalities)
                {
                    bool hasIntensity = IsKnownIntensity(municipality.Intensity);
                    DrawGeometry(
                        context,
                        ToPathGeometry(
                            GetRings(municipality.Rings, municipality.Coordinates),
                            projection),
                        hasIntensity
                            ? GetBrush(GetIntensityColor(municipality.Intensity, 150))
                            : null,
                        CreatePen(
                            hasIntensity
                                ? Color.FromArgb(225, 42, 50, 55)
                                : OutlineStroke,
                            0.8));
                }

                foreach (EarthquakeMapBoundaryLayer layer in visibleBoundaryLayers)
                {
                    if (layer.Boundaries.Length == 0 ||
                        !ShouldDrawIntensityBoundary(mapViewModel.ViewedReportType, layer.Intensity))
                    {
                        continue;
                    }

                    DrawGeometry(
                        context,
                        ToBoundaryPathGeometry(
                            layer.Boundaries,
                            projection,
                            boundarySimplificationPixels),
                        null,
                        CreatePen(
                            fillIntensityAreas
                                ? GetIntensityBorderColor(layer.Intensity, 245)
                                : GetIntensityColor(layer.Intensity, 245),
                            1.8));
                }

                });
            _staticGeometryHost = staticGeometryHost;
            _staticGeometryCacheKey = staticGeometryCacheKey;
        }
        double staticElapsed = Stopwatch.GetElapsedTime(staticStarted).TotalMilliseconds;
        if (HasBaseStaticGeometry(
                visibleOutline.Count,
                visibleAreas.Count,
                visibleMunicipalities.Count,
                visibleBoundaryCount))
        {
            MapContentCanvas.Children.Add(staticGeometryHost);
        }

        long selectionStarted = Stopwatch.GetTimestamp();
        if (visibleSelectedAreas.Count > 0 || visibleSelectedMunicipalities.Count > 0)
        {
            // 高亮单独绘制，选择变化不再使基础静态几何缓存失效。
            var selectionGeometryHost = new MapDrawingHost(
                MapCanvas.ActualWidth,
                MapCanvas.ActualHeight,
                context =>
                {
                    foreach (EarthquakeMapArea area in visibleSelectedAreas)
                    {
                        DrawSelectionGlow(
                            context,
                            GetRings(area.Rings, area.Coordinates),
                            projection,
                            area.Intensity);
                    }

                    foreach (EarthquakeMapMunicipality municipality in visibleSelectedMunicipalities)
                    {
                        DrawSelectionGlow(
                            context,
                            GetRings(municipality.Rings, municipality.Coordinates),
                            projection,
                            municipality.Intensity);
                    }
                });
            MapContentCanvas.Children.Add(selectionGeometryHost);
        }
        double selectionElapsed = Stopwatch.GetElapsedTime(selectionStarted).TotalMilliseconds;

        long markerStarted = Stopwatch.GetTimestamp();
        if (ShouldRenderMarkerHost(mapViewModel.Markers.Count, mapViewModel.SelectedStationHighlight is not null))
        {
            // 将观测点和震源统一绘制到一个 DrawingVisual，避免每个点创建多个控件。
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            bool showStationLabels = ShouldShowStationLabels(mapViewModel.ZoomLevel);
            PrepareMarkerDrawingCache(mapViewModel.Markers, showStationLabels, pixelsPerDip);
            var markerHost = new MapDrawingHost(
                MapCanvas.ActualWidth,
                MapCanvas.ActualHeight,
                context =>
                {
                    if (mapViewModel.SelectedStationHighlight is EarthquakeMapMarker selectedStation)
                    {
                        DrawSelectedMarkerGlow(context, selectedStation, projection);
                    }

                    foreach (EarthquakeMapMarker marker in OrderMarkersForRendering(mapViewModel.Markers))
                    {
                        DrawMarker(context, marker, projection, mapViewModel.ZoomLevel, pixelsPerDip);
                    }
                });
            MapContentCanvas.Children.Add(markerHost);
        }
        double markerElapsed = Stopwatch.GetElapsedTime(markerStarted).TotalMilliseconds;
        _renderedHighDetailBounds = renderBounds;

        TraceMap(
            "RenderComplete",
            projection.Unproject(new Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2)),
            $"elapsed={Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds:0.##}ms " +
            $"build={Stopwatch.GetElapsedTime(elementBuildStarted).TotalMilliseconds:0.##}ms " +
            $"children={MapContentCanvas.Children.Count} " +
            $"outline={visibleOutline.Count}/{mapViewModel.Outline.Length} " +
            $"areas={visibleAreas.Count}/{mapViewModel.Areas.Count} " +
            $"municipalities={visibleMunicipalities.Count}/{mapViewModel.Municipalities.Count} " +
            $"boundaries={visibleBoundaryCount}/" +
            $"{mapViewModel.BoundaryLayers.Sum(layer => layer.Boundaries.Length)} " +
            $"markers={mapViewModel.Markers.Count} " +
            $"markerTypes={CountMarkerDrawingTypes(mapViewModel.Markers)} " +
            $"boundarySimplify={boundarySimplificationPixels:0.##}px " +
            $"staticCache={(staticGeometryCacheHit ? "hit" : "miss")} " +
            $"stages=legend:{legendElapsed:0.##}ms,static:{staticElapsed:0.##}ms," +
            $"selection:{selectionElapsed:0.##}ms,markers:{markerElapsed:0.##}ms");
    }

    private StaticGeometryCacheKey CreateStaticGeometryCacheKey(
        EarthquakeMapViewModel mapViewModel,
        GeoCoordinate? selectedEventFocusCoordinate,
        MapGeometryBounds? selectedEventBounds)
    {
        return new StaticGeometryCacheKey(
            mapViewModel.ViewedReportKey,
            mapViewModel.DetailLevel,
            mapViewModel.ViewedReportType,
            mapViewModel.IsDistantEvent,
            mapViewModel.ZoomLevel,
            MapCanvas.ActualWidth,
            MapCanvas.ActualHeight,
            _viewportCenter,
            mapViewModel.FocusedCoordinate,
            selectedEventFocusCoordinate,
            selectedEventBounds,
            mapViewModel.Outline.Length,
            mapViewModel.Areas.Count,
            mapViewModel.Municipalities.Count,
            mapViewModel.BoundaryLayers.Count,
            0,
            0,
            null,
            null);
    }

    internal static bool ShouldReuseStaticGeometry(
        bool hasMatchingKey,
        bool hasCachedHost)
    {
        return hasMatchingKey && hasCachedHost;
    }

    private static bool AreEquivalentStaticGeometryKeys(
        StaticGeometryCacheKey left,
        StaticGeometryCacheKey right)
    {
        return string.Equals(left.ReportKey, right.ReportKey, StringComparison.Ordinal) &&
            left.DetailLevel == right.DetailLevel &&
            left.ReportType == right.ReportType &&
            left.IsDistantEvent == right.IsDistantEvent &&
            AreClose(left.ZoomLevel, right.ZoomLevel, 0.000001) &&
            AreClose(left.Width, right.Width, 0.01) &&
            AreClose(left.Height, right.Height, 0.01) &&
            AreClose(left.ViewportCenter, right.ViewportCenter, 0.000001) &&
            AreClose(left.FocusedCoordinate, right.FocusedCoordinate, 0.000001) &&
            AreClose(left.SelectedEventFocusCoordinate, right.SelectedEventFocusCoordinate, 0.000001) &&
            AreClose(left.SelectedEventBounds, right.SelectedEventBounds, 0.000001) &&
            left.OutlineCount == right.OutlineCount &&
            left.AreaCount == right.AreaCount &&
            left.MunicipalityCount == right.MunicipalityCount &&
            left.BoundaryCount == right.BoundaryCount &&
            left.SelectedAreaCount == right.SelectedAreaCount &&
            left.SelectedMunicipalityCount == right.SelectedMunicipalityCount &&
            left.SelectionKind == right.SelectionKind &&
            string.Equals(left.SelectionCode, right.SelectionCode, StringComparison.Ordinal);
    }

    private static string DescribeStaticGeometryCacheDifference(
        StaticGeometryCacheKey left,
        StaticGeometryCacheKey right)
    {
        var differences = new List<string>();
        if (!AreClose(left.ZoomLevel, right.ZoomLevel, 0.000001)) differences.Add("zoom");
        if (!AreClose(left.Width, right.Width, 0.01)) differences.Add("width");
        if (!AreClose(left.Height, right.Height, 0.01)) differences.Add("height");
        if (!AreClose(left.ViewportCenter, right.ViewportCenter, 0.000001)) differences.Add("viewport");
        if (!AreClose(left.FocusedCoordinate, right.FocusedCoordinate, 0.000001)) differences.Add("focus");
        if (!AreClose(left.SelectedEventFocusCoordinate, right.SelectedEventFocusCoordinate, 0.000001)) differences.Add("event-focus");
        if (!AreClose(left.SelectedEventBounds, right.SelectedEventBounds, 0.000001)) differences.Add("event-bounds");
        if (left.DetailLevel != right.DetailLevel) differences.Add("detail");
        if (left.ReportType != right.ReportType) differences.Add("report-type");
        if (left.IsDistantEvent != right.IsDistantEvent) differences.Add("distant");
        if (left.OutlineCount != right.OutlineCount) differences.Add("outline-count");
        if (left.AreaCount != right.AreaCount) differences.Add("area-count");
        if (left.MunicipalityCount != right.MunicipalityCount) differences.Add("municipality-count");
        if (left.BoundaryCount != right.BoundaryCount) differences.Add("boundary-count");
        if (left.SelectedAreaCount != right.SelectedAreaCount) differences.Add("selected-area-count");
        if (left.SelectedMunicipalityCount != right.SelectedMunicipalityCount) differences.Add("selected-municipality-count");
        if (left.SelectionKind != right.SelectionKind) differences.Add("selection-kind");
        if (!string.Equals(left.ReportKey, right.ReportKey, StringComparison.Ordinal)) differences.Add("report-key");
        if (!string.Equals(left.SelectionCode, right.SelectionCode, StringComparison.Ordinal)) differences.Add("selection-code");
        return $"fields={(differences.Count == 0 ? "unknown" : string.Join(',', differences))}";
    }

    private static bool AreClose(double left, double right, double tolerance)
    {
        return Math.Abs(left - right) <= tolerance;
    }

    internal static bool AreCloseValues(
        double left,
        double right,
        double tolerance)
    {
        return AreClose(left, right, tolerance);
    }

    private static bool AreClose(
        GeoCoordinate? left,
        GeoCoordinate? right,
        double tolerance)
    {
        return left is GeoCoordinate leftValue && right is GeoCoordinate rightValue
            ? AreClose(leftValue.Latitude, rightValue.Latitude, tolerance) &&
                AreClose(leftValue.Longitude, rightValue.Longitude, tolerance)
            : left is null && right is null;
    }

    private static bool AreClose(
        MapGeometryBounds? left,
        MapGeometryBounds? right,
        double tolerance)
    {
        return left is MapGeometryBounds leftValue && right is MapGeometryBounds rightValue
            ? AreClose(leftValue.MinLongitude, rightValue.MinLongitude, tolerance) &&
                AreClose(leftValue.MaxLongitude, rightValue.MaxLongitude, tolerance) &&
                AreClose(leftValue.MinLatitude, rightValue.MinLatitude, tolerance) &&
                AreClose(leftValue.MaxLatitude, rightValue.MaxLatitude, tolerance)
            : left is null && right is null;
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
            $" cache={(MapContentCanvas.CacheMode is null ? "off" : "on")}" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}");
        Console.WriteLine(message);
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
        MapProjection projection,
        double minimumPixelDistance)
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

                Point firstPoint = projection.Project(boundary.Coordinates[0]);
                context.BeginFigure(
                    firstPoint,
                    isFilled: false,
                    isClosed: false);
                Point previousPoint = firstPoint;
                for (int index = 1; index < boundary.Coordinates.Length; index++)
                {
                    Point projectedPoint = projection.Project(boundary.Coordinates[index]);
                    bool isLastPoint = index == boundary.Coordinates.Length - 1;
                    if (ShouldKeepBoundaryPoint(
                            previousPoint,
                            projectedPoint,
                            isLastPoint,
                            minimumPixelDistance))
                    {
                        context.LineTo(
                            projectedPoint,
                            isStroked: true,
                            isSmoothJoin: false);
                        previousPoint = projectedPoint;
                    }
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    internal static bool ShouldKeepBoundaryPoint(
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

    private void DrawMarker(
        DrawingContext context,
        EarthquakeMapMarker marker,
        MapProjection projection,
        double zoomLevel,
        double pixelsPerDip)
    {
        Point point = projection.Project(marker.Coordinate);
        bool showStationLabel = marker.Kind == EarthquakeMapMarkerKind.Station &&
            ShouldShowStationLabels(zoomLevel);
        DrawingGroup drawing = GetMarkerDrawing(
            marker,
            showStationLabel,
            pixelsPerDip);
        context.PushTransform(new TranslateTransform(point.X, point.Y));
        context.DrawDrawing(drawing);
        context.Pop();
    }

    private DrawingGroup GetMarkerDrawing(
        EarthquakeMapMarker marker,
        bool showStationLabel,
        double pixelsPerDip)
    {
        var key = new MarkerDrawingKey(
            marker.Kind,
            marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? JmaIntensity.Unknown
                : marker.Intensity,
            showStationLabel,
            pixelsPerDip);
        if (_markerDrawingCache.TryGetValue(key, out DrawingGroup? cachedDrawing))
        {
            return cachedDrawing;
        }

        double size = GetMarkerSize(marker.Kind, showStationLabel);
        Brush fill = marker.Kind == EarthquakeMapMarkerKind.Hypocenter
            ? GetBrush(Color.FromRgb(190, 61, 52))
            : GetBrush(GetIntensityColor(marker.Intensity, 245));
        Pen stroke = CreatePen(
            marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                ? Colors.White
                : GetIntensityBorderColor(marker.Intensity, 245),
            1.5);
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
        {
            context.DrawEllipse(fill, stroke, new Point(), size / 2, size / 2);
            if (showStationLabel)
            {
                FormattedText formattedText = GetStationLabelText(marker.Intensity, pixelsPerDip);
                context.DrawText(
                    formattedText,
                    new Point(-formattedText.Width / 2, -formattedText.Height / 2));
            }
        }

        drawing.Freeze();
        _markerDrawingCache[key] = drawing;
        return drawing;
    }

    private void PrepareMarkerDrawingCache(
        IReadOnlyList<EarthquakeMapMarker> markers,
        bool showStationLabels,
        double pixelsPerDip)
    {
        string? reportKey = ViewModel?.ViewedReportKey;
        if (string.Equals(_markerCacheReportKey, reportKey, StringComparison.Ordinal) &&
            _markerCacheShowLabels == showStationLabels &&
            _markerCachePixelsPerDip.Equals(pixelsPerDip))
        {
            return;
        }

        _markerDrawingCache.Clear();
        _markerCacheReportKey = reportKey;
        _markerCacheShowLabels = showStationLabels;
        _markerCachePixelsPerDip = pixelsPerDip;
        foreach (EarthquakeMapMarker marker in markers
                     .GroupBy(marker => (
                         marker.Kind,
                         Intensity: marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                             ? JmaIntensity.Unknown
                             : marker.Intensity))
                     .Select(group => group.First()))
        {
            GetMarkerDrawing(marker, showStationLabels, pixelsPerDip);
        }
    }

    internal static FormattedText GetStationLabelText(
        JmaIntensity intensity,
        double pixelsPerDip)
    {
        var key = (intensity, pixelsPerDip);
        if (StationLabelTextCache.TryGetValue(key, out FormattedText? cachedText))
        {
            return cachedText;
        }

        var formattedText = new FormattedText(
            GetStationMarkerText(intensity) ?? "?",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            StationLabelTypeface,
            10,
            GetBrush(GetIntensityTextColor(intensity)),
            pixelsPerDip);
        StationLabelTextCache[key] = formattedText;
        return formattedText;
    }

    internal static double GetMarkerSize(EarthquakeMapMarkerKind kind, bool showStationLabel)
    {
        return kind == EarthquakeMapMarkerKind.Hypocenter
            ? 15
            : showStationLabel ? 20 : 8;
    }

    internal static bool ShouldRenderMarkerHost(int markerCount, bool hasSelectedStation)
    {
        return markerCount > 0 || hasSelectedStation;
    }

    internal static int CountMarkerDrawingTypes(IEnumerable<EarthquakeMapMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        return markers
            .Select(marker => (
                marker.Kind,
                Intensity: marker.Kind == EarthquakeMapMarkerKind.Hypocenter
                    ? JmaIntensity.Unknown
                    : marker.Intensity))
            .Distinct()
            .Count();
    }

    internal static bool ShouldShowStationLabels(double zoomLevel)
    {
        return double.IsFinite(zoomLevel) && zoomLevel >= StationLabelZoomThreshold;
    }

    internal static string? GetStationMarkerText(JmaIntensity intensity)
    {
        return IsKnownIntensity(intensity) ? GetIntensityLegendText(intensity) : "?";
    }

    internal static Color GetIntensityTextColor(JmaIntensity intensity)
    {
        Color fill = GetIntensityColor(intensity, 255);
        double luminance =
            (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255;
        return luminance > 0.58 ? Colors.Black : Colors.White;
    }

    private static void DrawGeometry(
        DrawingContext context,
        StreamGeometry geometry,
        Brush? fill,
        Pen? stroke)
    {
        context.DrawGeometry(fill, stroke, geometry);
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        int colorKey = color.A << 24 | color.R << 16 | color.G << 8 | color.B;
        if (PenCache.TryGetValue((colorKey, thickness), out Pen? cachedPen))
        {
            return cachedPen;
        }

        var pen = new Pen(GetBrush(color), thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        PenCache[(colorKey, thickness)] = pen;
        return pen;
    }

    private static void DrawSelectionGlow(
        DrawingContext context,
        IReadOnlyList<ImmutableArray<GeoCoordinate>> rings,
        MapProjection projection,
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
        context.DrawGeometry(
            intensity is JmaIntensity known && IsKnownIntensity(known)
                ? GetBrush(GetIntensityColor(known, 150))
                : null,
            null,
            geometry);
        context.DrawGeometry(null, CreatePen(outerColor, 8), geometry);
        context.DrawGeometry(null, CreatePen(innerColor, 2.4), geometry);
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

    private static void DrawSelectedMarkerGlow(
        DrawingContext context,
        EarthquakeMapMarker marker,
        MapProjection projection)
    {
        Point point = projection.Project(marker.Coordinate);
        (Color outerColor, Color innerColor) = GetSelectionColors(marker.Intensity);
        context.DrawEllipse(null, CreatePen(outerColor, 5), point, 12, 12);
        context.DrawEllipse(null, CreatePen(innerColor, 2), point, 6.5, 6.5);
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

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _visual;
        }
    }

    private readonly record struct MarkerDrawingKey(
        EarthquakeMapMarkerKind Kind,
        JmaIntensity Intensity,
        bool ShowStationLabel,
        double PixelsPerDip);

    private readonly record struct StaticGeometryCacheKey(
        string? ReportKey,
        MapDetailLevel DetailLevel,
        EarthquakeReportType ReportType,
        bool IsDistantEvent,
        double ZoomLevel,
        double Width,
        double Height,
        GeoCoordinate? ViewportCenter,
        GeoCoordinate? FocusedCoordinate,
        GeoCoordinate? SelectedEventFocusCoordinate,
        MapGeometryBounds? SelectedEventBounds,
        int OutlineCount,
        int AreaCount,
        int MunicipalityCount,
        int BoundaryCount,
        int SelectedAreaCount,
        int SelectedMunicipalityCount,
        EarthquakeMapSelectionKind? SelectionKind,
        string? SelectionCode);

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
