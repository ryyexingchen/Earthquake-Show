using System.Windows;
using System.Windows.Media;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.App.Views;
using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class EarthquakeMapProjectionTests
{
    [Fact]
    public void PreferredCenterIsPreservedWhenGeometryBoundsChange()
    {
        MapPolygonGeometry oldPolygon = new(
            "old",
            "旧几何",
            [
                new GeoCoordinate(32, 130),
                new GeoCoordinate(32, 131),
                new GeoCoordinate(33, 131),
                new GeoCoordinate(33, 130),
            ],
            true);
        MapPolygonGeometry newPolygon = new(
            "new",
            "新几何",
            [
                new GeoCoordinate(31, 129),
                new GeoCoordinate(31, 132),
                new GeoCoordinate(34, 132),
                new GeoCoordinate(34, 129),
            ],
            true);

        EarthquakeMapView.MapProjection before = EarthquakeMapView.MapProjection.Create(
            [oldPolygon],
            [],
            [],
            EarthquakeMapFocusMode.JapanOverview,
            null,
            null,
            null,
            null,
            4,
            1000,
            600,
            80,
            -30);
        GeoCoordinate currentCenter = before.Unproject(new Point(500, 300));

        EarthquakeMapView.MapProjection after = EarthquakeMapView.MapProjection.Create(
            [newPolygon],
            [],
            [],
            EarthquakeMapFocusMode.JapanOverview,
            null,
            null,
            null,
            currentCenter,
            4,
            1000,
            600,
            0,
            0);

        Point projectedCenter = after.Project(currentCenter);
        Assert.Equal(500, projectedCenter.X, precision: 6);
        Assert.Equal(300, projectedCenter.Y, precision: 6);
    }

    [Fact]
    public void DistantProjection_UnprojectWrapsLongitudeAcrossDateLine()
    {
        GeoCoordinate hypocenter = new(-15.4, 167.8);
        EarthquakeMapView.MapProjection projection = EarthquakeMapView.MapProjection.Create(
            [],
            [],
            [new EarthquakeMapMarker(
                EarthquakeMapMarkerKind.Hypocenter,
                "远地震源",
                hypocenter,
                JmaIntensity.Unknown)],
            EarthquakeMapFocusMode.SelectedEvent,
            null,
            hypocenter,
            new MapGeometryBounds(167.675, 167.925, -15.525, -15.275),
            null,
            EarthquakeMapViewModel.MaxSmallZoomLevel,
            1000,
            600,
            0,
            0,
            new MapGeometryBounds(126, 147, 24, 47));

        GeoCoordinate coordinate = projection.Unproject(new Point(1000, 300));

        Assert.InRange(coordinate.Longitude, -180, 180);
        Assert.True(coordinate.Longitude < 0);
    }

    [Fact]
    public void RepeatedFocusNotificationDoesNotResetPan()
    {
        GeoCoordinate coordinate = new(35.25, 139.75);

        Assert.False(EarthquakeMapView.ShouldResetPanForFocusedCoordinate(coordinate, coordinate));
        Assert.True(EarthquakeMapView.ShouldResetPanForFocusedCoordinate(null, coordinate));
    }

    [Fact]
    public void GeometryCenter_UsesProjectedCenterAfterLoad()
    {
        GeoCoordinate projected = new(35.2, 139.7);

        Assert.Equal(
            projected,
            EarthquakeMapView.SelectGeometryCenter(
                new GeoCoordinate(35, 139),
                new GeoCoordinate(34, 138),
                projected));
    }

    [Fact]
    public void GeometryCenter_UsesCommittedCenterDuringPan()
    {
        GeoCoordinate committed = new(35, 139);

        Assert.Equal(
            committed,
            EarthquakeMapView.SelectGeometryCenter(
                committed,
                new GeoCoordinate(34, 138),
                new GeoCoordinate(35.2, 139.7),
                isPanning: true));
    }

    [Fact]
    public void GeometryCenter_FallsBackToPreferredWhenPanHasNoCommittedCenter()
    {
        GeoCoordinate preferred = new(34, 138);

        Assert.Equal(
            preferred,
            EarthquakeMapView.SelectGeometryCenter(
                null,
                preferred,
                new GeoCoordinate(35.2, 139.7),
                isPanning: true));
    }

    [Fact]
    public void FollowStateResetOnlyMatchesFollowStateProperties()
    {
        Assert.True(EarthquakeMapView.ShouldResetPanForFollowState(
            nameof(EarthquakeMapViewModel.EffectiveFocusMode)));
        Assert.False(EarthquakeMapView.ShouldResetPanForFollowState(
            nameof(EarthquakeMapViewModel.ZoomLevel)));
    }

    [Fact]
    public void SelectionGlow_IgnoresEmptyOrIncompleteRings()
    {
        Assert.False(EarthquakeMapView.IsRenderableRing([]));
        Assert.False(EarthquakeMapView.IsRenderableRing(
            [new GeoCoordinate(35, 139), new GeoCoordinate(35.1, 139.1)]));
        Assert.True(EarthquakeMapView.IsRenderableRing(
            [
                new GeoCoordinate(35, 139),
                new GeoCoordinate(35.1, 139),
                new GeoCoordinate(35, 139.1),
            ]));
    }

    [Fact]
    public void IsRenderableBoundaryUsesMinimumPointCountAndFiniteCoordinates()
    {
        Assert.False(EarthquakeMapView.IsRenderableBoundary([]));
        Assert.False(EarthquakeMapView.IsRenderableBoundary([
            new GeoCoordinate(35, 139)]));
        Assert.True(EarthquakeMapView.IsRenderableBoundary([
            new GeoCoordinate(35, 139),
            new GeoCoordinate(35.1, 139.1)]));
    }

    [Fact]
    public void FilterVisibleBoundaryLayersPreservesLayerAndBoundaryOrder()
    {
        EarthquakeMapBoundary first = new(
            "A",
            "B",
            [new GeoCoordinate(35, 139), new GeoCoordinate(35.1, 139.1)]);
        EarthquakeMapBoundary second = new(
            "C",
            "D",
            [new GeoCoordinate(40, 145), new GeoCoordinate(40.1, 145.1)]);
        IReadOnlyList<EarthquakeMapBoundaryLayer> result =
            EarthquakeMapView.FilterVisibleBoundaryLayers(
                [
                    new EarthquakeMapBoundaryLayer(JmaIntensity.Two, [first, second]),
                ],
                new MapGeometryBounds(138, 140, 34, 36));

        EarthquakeMapBoundaryLayer layer = Assert.Single(result);
        Assert.Single(layer.Boundaries);
        Assert.Equal(first, layer.Boundaries[0]);
    }

    [Fact]
    public void FilterVisibleItemsPreservesOrderAndKeepsOnlyIntersectingItems()
    {
        IReadOnlyList<(string Name, MapGeometryBounds Bounds)> items =
        [
            ("first", new MapGeometryBounds(138, 140, 34, 36)),
            ("outside", new MapGeometryBounds(145, 146, 40, 41)),
            ("second", new MapGeometryBounds(139, 141, 35, 37)),
        ];

        IReadOnlyList<(string Name, MapGeometryBounds Bounds)> result =
            EarthquakeMapView.FilterVisibleItems(
                items,
                new MapGeometryBounds(139.5, 140.5, 35.5, 36.5),
                static item => item.Bounds);

        Assert.Equal(["first", "second"], result.Select(item => item.Name));
    }

    [Fact]
    public void LegendIntensities_UsesContinuousKnownRangeWithoutUnknown()
    {
        Assert.Equal(
            [JmaIntensity.One, JmaIntensity.Two],
            EarthquakeMapView.BuildLegendIntensities([JmaIntensity.Two]));
    }

    [Fact]
    public void LegendIntensities_ShowsUnknownOnlyWhenPresent()
    {
        Assert.Equal(
            [JmaIntensity.Unknown, JmaIntensity.One, JmaIntensity.Two, JmaIntensity.Three],
            EarthquakeMapView.BuildLegendIntensities(
                [JmaIntensity.Unknown, JmaIntensity.Three]));
    }

    [Theory]
    [InlineData(JmaIntensity.FiveLower, "5-")]
    [InlineData(JmaIntensity.FiveUpper, "5+")]
    [InlineData(JmaIntensity.SixLower, "6-")]
    [InlineData(JmaIntensity.SixUpper, "6+")]
    [InlineData(JmaIntensity.Three, "3")]
    [InlineData(JmaIntensity.Unknown, "不明")]
    public void LegendText_UsesDisplayCodesForJmaHalfIntensities(
        JmaIntensity intensity,
        string expected)
    {
        Assert.Equal(expected, EarthquakeMapView.GetIntensityLegendText(intensity));
    }

    [Theory]
    [InlineData(7.99, false)]
    [InlineData(8, true)]
    [InlineData(12, true)]
    public void StationLabels_UseZoomThreshold(double zoomLevel, bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldShowStationLabels(zoomLevel));
    }

    [Theory]
    [InlineData(EarthquakeMapMarkerKind.Station, false, 8)]
    [InlineData(EarthquakeMapMarkerKind.Station, true, 20)]
    [InlineData(EarthquakeMapMarkerKind.Hypocenter, false, 15)]
    [InlineData(EarthquakeMapMarkerKind.Hypocenter, true, 15)]
    public void MarkerSize_PreservesLowAndHighDetailPresentation(
        EarthquakeMapMarkerKind kind,
        bool showStationLabel,
        double expected)
    {
        Assert.Equal(expected, EarthquakeMapView.GetMarkerSize(kind, showStationLabel));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    public void MarkerDrawingHost_IsCreatedOnlyWhenMarkersExist(
        int markerCount,
        bool hasSelectedStation,
        bool expected)
    {
        Assert.Equal(expected,
            EarthquakeMapView.ShouldRenderMarkerHost(markerCount, hasSelectedStation));
    }

    [Fact]
    public void MarkerDrawingTypes_OnlyIncludesTypesPresentInCurrentReport()
    {
        EarthquakeMapMarker[] markers =
        [
            new(EarthquakeMapMarkerKind.Station, "一", new GeoCoordinate(35, 139), JmaIntensity.Three),
            new(EarthquakeMapMarkerKind.Station, "二", new GeoCoordinate(35.1, 139.1), JmaIntensity.Three),
            new(EarthquakeMapMarkerKind.Station, "不明", new GeoCoordinate(35.2, 139.2), JmaIntensity.Unknown),
            new(EarthquakeMapMarkerKind.Hypocenter, "震源", new GeoCoordinate(35.3, 139.3), JmaIntensity.Unknown),
        ];

        Assert.Equal(3, EarthquakeMapView.CountMarkerDrawingTypes(markers));
    }

    [Fact]
    public void MarkerRendering_DrawsHypocenterAfterStationLabels()
    {
        EarthquakeMapMarker station = new(
            EarthquakeMapMarkerKind.Station,
            "观测点",
            new GeoCoordinate(35, 139),
            JmaIntensity.Three);
        EarthquakeMapMarker hypocenter = new(
            EarthquakeMapMarkerKind.Hypocenter,
            "震源",
            new GeoCoordinate(35, 139),
            JmaIntensity.Three);

        Assert.Equal(
            [EarthquakeMapMarkerKind.Station, EarthquakeMapMarkerKind.Hypocenter],
            EarthquakeMapView.OrderMarkersForRendering([hypocenter, station])
                .Select(marker => marker.Kind));
    }

    [Theory]
    [InlineData(JmaIntensity.One, "1")]
    [InlineData(JmaIntensity.FiveLower, "5-")]
    [InlineData(JmaIntensity.FiveUpper, "5+")]
    [InlineData(JmaIntensity.SixLower, "6-")]
    [InlineData(JmaIntensity.SixUpper, "6+")]
    [InlineData(JmaIntensity.Seven, "7")]
    public void StationMarkerText_UsesStandardIntensityLabels(
        JmaIntensity intensity,
        string expected)
    {
        Assert.Equal(expected, EarthquakeMapView.GetStationMarkerText(intensity));
    }

    [Fact]
    public void StationMarkerText_UnknownIntensityUsesQuestionMark()
    {
        Assert.Equal("?", EarthquakeMapView.GetStationMarkerText(JmaIntensity.Unknown));
    }

    [Fact]
    public void StationLabelText_IsReusedForSameIntensityAndDpi()
    {
        FormattedText first = EarthquakeMapView.GetStationLabelText(JmaIntensity.Three, 1);
        FormattedText second = EarthquakeMapView.GetStationLabelText(JmaIntensity.Three, 1);

        Assert.Same(first, second);
        Assert.NotSame(
            first,
            EarthquakeMapView.GetStationLabelText(JmaIntensity.Four, 1));
    }

    [Theory]
    [InlineData(JmaIntensity.One, 0, 0, 0)]
    [InlineData(JmaIntensity.Three, 0, 0, 0)]
    [InlineData(JmaIntensity.SixUpper, 255, 255, 255)]
    public void StationMarkerTextColor_ContrastsWithIntensityFill(
        JmaIntensity intensity,
        byte red,
        byte green,
        byte blue)
    {
        Assert.Equal(Color.FromRgb(red, green, blue),
            EarthquakeMapView.GetIntensityTextColor(intensity));
    }

    [Theory]
    [InlineData(EarthquakeReportType.SeismicIntensity, true)]
    [InlineData(EarthquakeReportType.Hypocenter, true)]
    [InlineData(EarthquakeReportType.HypocenterAndIntensity, false)]
    [InlineData(EarthquakeReportType.DistantEarthquake, false)]
    [InlineData(EarthquakeReportType.Unknown, false)]
    public void AreaFill_DependsOnViewedReportType(
        EarthquakeReportType reportType,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldFillIntensityAreas(reportType));
    }

    [Fact]
    public void AreaFill_HidesUnknownAreasAndBoundaries()
    {
        Assert.False(EarthquakeMapView.ShouldDrawIntensityArea(
            EarthquakeReportType.SeismicIntensity,
            JmaIntensity.Unknown));
        Assert.False(EarthquakeMapView.ShouldDrawIntensityBoundary(
            EarthquakeReportType.SeismicIntensity,
            JmaIntensity.Unknown));
        Assert.False(EarthquakeMapView.ShouldDrawIntensityBoundary(
            EarthquakeReportType.Hypocenter,
            JmaIntensity.Unknown));
        Assert.True(EarthquakeMapView.ShouldDrawIntensityArea(
            EarthquakeReportType.SeismicIntensity,
            JmaIntensity.Three));
        Assert.True(EarthquakeMapView.ShouldDrawIntensityBoundary(
            EarthquakeReportType.HypocenterAndIntensity,
            JmaIntensity.Unknown));
    }

    [Fact]
    public void HighDetailReloadStartsOnlyOutsideLoadedViewport()
    {
        MapGeometryBounds loaded = new(129, 132, 31, 35);

        Assert.False(EarthquakeMapViewModel.NeedsHighDetailReload(
            MapDetailLevel.High,
            loaded,
            new MapGeometryBounds(130, 131, 32, 34)));
        Assert.True(EarthquakeMapViewModel.NeedsHighDetailReload(
            MapDetailLevel.High,
            loaded,
            new MapGeometryBounds(132, 135, 32, 35)));
        Assert.False(EarthquakeMapViewModel.NeedsHighDetailReload(
            MapDetailLevel.Medium,
            loaded,
            new MapGeometryBounds(132, 135, 32, 35)));
    }

    [Fact]
    public void HighDetailReloadUsesCacheTileBoundaries()
    {
        MapGeometryBounds loaded = new(130, 131, 32, 33);

        Assert.False(EarthquakeMapViewModel.NeedsHighDetailReload(
            MapDetailLevel.High,
            loaded,
            new MapGeometryBounds(130.05, 130.95, 32.05, 32.95)));
        Assert.True(EarthquakeMapViewModel.NeedsHighDetailReload(
            MapDetailLevel.High,
            loaded,
            new MapGeometryBounds(130.95, 131.05, 32.05, 32.95)));
    }

    [Fact]
    public void HighCacheTile_ContainsRequestedViewport()
    {
        MapGeometryBounds requested = new(130.2, 130.8, 32.2, 32.8);

        MapGeometryBounds cached = MapLodResourceProvider.ExpandToHighCacheTile(requested);

        Assert.Equal(130, cached.MinLongitude);
        Assert.Equal(131, cached.MaxLongitude);
        Assert.Equal(32, cached.MinLatitude);
        Assert.Equal(33, cached.MaxLatitude);
        Assert.True(cached.MinLongitude <= requested.MinLongitude);
        Assert.True(cached.MaxLongitude >= requested.MaxLongitude);
        Assert.True(cached.MinLatitude <= requested.MinLatitude);
        Assert.True(cached.MaxLatitude >= requested.MaxLatitude);
    }

    [Fact]
    public void InFlightHighDetailLoadIsReusedForContainedViewport()
    {
        MapGeometryBounds loading = new(129, 132, 31, 35);

        Assert.True(EarthquakeMapViewModel.ShouldReuseInFlightHighLoad(
            true,
            MapDetailLevel.High,
            loading,
            MapDetailLevel.High,
            new MapGeometryBounds(130, 131, 32, 34)));
        Assert.False(EarthquakeMapViewModel.ShouldReuseInFlightHighLoad(
            true,
            MapDetailLevel.High,
            loading,
            MapDetailLevel.High,
            new MapGeometryBounds(132, 135, 32, 35)));
        Assert.False(EarthquakeMapViewModel.ShouldReuseInFlightHighLoad(
            true,
            MapDetailLevel.Medium,
            loading,
            MapDetailLevel.High,
            new MapGeometryBounds(130, 131, 32, 34)));
    }

    [Theory]
    [InlineData(true, "Medium", "Medium", true)]
    [InlineData(true, "Overview", "Medium", false)]
    [InlineData(false, "Medium", "Medium", false)]
    public void InFlightNonHighDetailLoadIsReused(
        bool isLoading,
        string loadingLevel,
        string desiredLevel,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapViewModel.ShouldReuseInFlightDetailLoad(
            isLoading,
            Enum.Parse<MapDetailLevel>(loadingLevel),
            Enum.Parse<MapDetailLevel>(desiredLevel)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RenderIsDeferredWhilePanning(bool isPanning, bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldDeferRenderDuringPan(isPanning));
    }

    [Theory]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, false, true, false)]
    public void PanVisualReuseRequiresStableContentDetailAndRenderCoverage(
        bool isPanning,
        bool contentChanged,
        bool detailWillChange,
        bool renderedCoverageContainsViewport,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldReusePanVisual(
            isPanning,
            contentChanged,
            detailWillChange,
            renderedCoverageContainsViewport));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0, 0, true)]
    [InlineData(0, 0, 0, 0, 0, 0, false)]
    public void StaticMapLayersUseOneDrawingHostWhenGeometryExists(
        int outlineCount,
        int areaCount,
        int municipalityCount,
        int boundaryCount,
        int selectedAreaCount,
        int selectedMunicipalityCount,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.HasStaticGeometry(
            outlineCount,
            areaCount,
            municipalityCount,
            boundaryCount,
            selectedAreaCount,
            selectedMunicipalityCount));
    }

    [Fact]
    public void StaticGeometryCacheUsesToleranceForEquivalentProjectionValues()
    {
        Assert.True(EarthquakeMapView.AreCloseValues(1.0000005, 1, 0.000001));
        Assert.False(EarthquakeMapView.AreCloseValues(1.000002, 1, 0.000001));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(0, 0, 0, 1, true)]
    [InlineData(0, 1, 0, 0, true)]
    public void BaseStaticLayerIgnoresSelectionOnlyGeometry(
        int outlineCount,
        int areaCount,
        int municipalityCount,
        int boundaryCount,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.HasBaseStaticGeometry(
            outlineCount,
            areaCount,
            municipalityCount,
            boundaryCount));
    }

    [Theory]
    [InlineData(0.4, 0.3, false, 0.65, false)]
    [InlineData(0.4, 0.3, true, 0.65, true)]
    [InlineData(0.8, 0, false, 0.65, true)]
    [InlineData(0.1, 0.1, false, 0, true)]
    public void BoundaryPointSimplificationKeepsVisibleDistanceAndEndpoints(
        double deltaX,
        double deltaY,
        bool isLastPoint,
        double minimumPixelDistance,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldKeepBoundaryPoint(
            new Point(0, 0),
            new Point(deltaX, deltaY),
            isLastPoint,
            minimumPixelDistance));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void StaticGeometryReuseRequiresMatchingKeyAndCachedHost(
        bool hasMatchingKey,
        bool hasCachedHost,
        bool expected)
    {
        Assert.Equal(expected,
            EarthquakeMapView.ShouldReuseStaticGeometry(hasMatchingKey, hasCachedHost));
    }

    [Fact]
    public void HighDetailRenderBoundsIncludeViewportBuffer()
    {
        MapGeometryBounds bounds = EarthquakeMapView.ExpandRenderBounds(
            new MapGeometryBounds(130, 132, 32, 33),
            EarthquakeMapView.HighDetailRenderBufferRatio);

        Assert.Equal(129.5, bounds.MinLongitude, precision: 6);
        Assert.Equal(132.5, bounds.MaxLongitude, precision: 6);
        Assert.Equal(31.75, bounds.MinLatitude, precision: 6);
        Assert.Equal(33.25, bounds.MaxLatitude, precision: 6);
    }

    [Theory]
    [InlineData(129, 130, 31, 32, true)]
    [InlineData(132, 133, 33, 34, true)]
    [InlineData(133.01, 134, 33, 34, false)]
    public void GeometryBoundsIntersectionSupportsViewportCulling(
        double minLongitude,
        double maxLongitude,
        double minLatitude,
        double maxLatitude,
        bool expected)
    {
        MapGeometryBounds viewport = new(130, 133, 32, 33);
        MapGeometryBounds geometry = new(
            minLongitude,
            maxLongitude,
            minLatitude,
            maxLatitude);

        Assert.Equal(expected, geometry.Intersects(viewport));
    }

    [Fact]
    public void GeometryBoundsCombineWithoutRescanningCoordinates()
    {
        MapGeometryBounds combined = MapGeometryBounds.FromBounds([
            new MapGeometryBounds(130, 131, 32, 33),
            new MapGeometryBounds(129, 132, 31, 34),
        ]);

        Assert.Equal(new MapGeometryBounds(129, 132, 31, 34), combined);
    }

    [Fact]
    public void RenderCoverageDetectsCumulativePanOutsideBufferedViewport()
    {
        MapGeometryBounds rendered = new(129.5, 132.5, 31.75, 33.25);

        Assert.True(rendered.Contains(new MapGeometryBounds(130, 132, 32, 33)));
        Assert.False(rendered.Contains(new MapGeometryBounds(130.8, 132.8, 32, 33)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void PanCacheIsKeptOnlyWhileViewIsLoaded(bool isLoaded, bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldKeepPanCacheAfterInteraction(isLoaded));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void RenderIsDeferredDuringPanOrWheelZoom(
        bool isPanning,
        bool isWheelZooming,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapView.ShouldDeferRenderDuringInteraction(
            isPanning,
            isWheelZooming));
    }

    [Theory]
    [InlineData(true, false, MapDetailLevel.High, false, true, true)]
    [InlineData(true, false, MapDetailLevel.High, true, true, false)]
    [InlineData(true, false, MapDetailLevel.Medium, false, true, false)]
    [InlineData(false, false, MapDetailLevel.High, false, true, false)]
    [InlineData(true, true, MapDetailLevel.High, false, true, false)]
    [InlineData(true, false, MapDetailLevel.High, false, false, false)]
    public void HighDetailReloadRenderIsDeferredOnlyWhenNewGeometryIsRequired(
        bool isPanning,
        bool contentChanged,
        MapDetailLevel detailLevel,
        bool renderedCoverageContainsViewport,
        bool willChangeDetailLevel,
        bool expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.ShouldDeferHighDetailRenderAfterPan(
                isPanning,
                contentChanged,
                detailLevel,
                renderedCoverageContainsViewport,
                willChangeDetailLevel));
    }

    [Theory]
    [InlineData(true, false, MapDetailLevel.High, false, true)]
    [InlineData(true, false, MapDetailLevel.High, true, false)]
    [InlineData(true, false, MapDetailLevel.Medium, false, false)]
    [InlineData(true, true, MapDetailLevel.High, false, false)]
    [InlineData(false, false, MapDetailLevel.High, false, false)]
    public void HighDetailCoverageRedrawIsDeferredOnlyAfterCoverageExpires(
        bool isPanning,
        bool contentChanged,
        MapDetailLevel detailLevel,
        bool renderedCoverageContainsViewport,
        bool expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.ShouldDeferHighDetailCoverageRenderAfterPan(
                isPanning,
                contentChanged,
                detailLevel,
                renderedCoverageContainsViewport));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DetailLevelCheckIsDeferredDuringPan(bool isPanning, bool expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.ShouldDeferDetailCheckDuringPan(isPanning));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DetailLevelCheckIsQueuedOnlyOncePerDispatchCycle(
        bool dispatchPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.ShouldQueueDetailLevelCheck(dispatchPending));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ReportChangeRenderIsDeferredOnlyForDetailDecreaseOutsidePan(
        bool isPanning,
        bool willDecreaseDetailLevel,
        bool expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.ShouldDeferReportChangeRender(
                isPanning,
                willDecreaseDetailLevel));
    }

    [Theory]
    [InlineData(MapDetailLevel.Overview, 12000, 0)]
    [InlineData(MapDetailLevel.Medium, 12000, 0)]
    [InlineData(MapDetailLevel.High, 7999, EarthquakeMapView.HighDetailBoundarySimplificationPixels)]
    [InlineData(MapDetailLevel.High, 8000, EarthquakeMapView.DenseHighDetailBoundarySimplificationPixels)]
    public void BoundarySimplificationAdaptsToVisibleBoundaryDensity(
        MapDetailLevel detailLevel,
        int visibleBoundaryCount,
        double expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.GetBoundarySimplificationPixels(
                detailLevel,
                visibleBoundaryCount));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 1.25)]
    [InlineData(2, 1, 0.8)]
    public void WheelPreviewScaleFollowsGlobalZoomLevel(
        double baseZoomLevel,
        double currentZoomLevel,
        double expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapView.GetWheelPreviewScale(baseZoomLevel, currentZoomLevel),
            precision: 6);
    }

    [Theory]
    [InlineData(MapDetailLevel.High, MapDetailLevel.Medium, true)]
    [InlineData(MapDetailLevel.High, MapDetailLevel.Overview, true)]
    [InlineData(MapDetailLevel.Medium, MapDetailLevel.Overview, true)]
    [InlineData(MapDetailLevel.Medium, MapDetailLevel.High, false)]
    [InlineData(MapDetailLevel.Medium, MapDetailLevel.Medium, false)]
    public void DetailLevelDecreaseIsDistinguishedFromUpgradeAndSameLevel(
        MapDetailLevel currentLevel,
        MapDetailLevel desiredLevel,
        bool expected)
    {
        Assert.Equal(
            expected,
            EarthquakeMapViewModel.IsDetailLevelDecrease(currentLevel, desiredLevel));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void ReportChangeAutoscaleIsSkippedDuringManualPan(
        bool isApplyingAutoScale,
        bool isMapPanning,
        bool expected)
    {
        Assert.Equal(expected, EarthquakeMapViewModel.ShouldAutoScaleAfterReportChange(
            isApplyingAutoScale,
            isMapPanning,
            reportChanged: true));
    }

    [Fact]
    public void GlobalZoomLevelUsesOverviewScaleAndProgressesByOnePointTwentyFive()
    {
        MapPolygonGeometry eventPolygon = new(
            "event",
            "事件区域",
            [
                new GeoCoordinate(32.4, 130.4),
                new GeoCoordinate(32.4, 131.0),
                new GeoCoordinate(33.1, 131.0),
                new GeoCoordinate(33.1, 130.4),
            ],
            true);
        MapGeometryBounds overviewBounds = new(125, 150, 20, 50);
        EarthquakeMapView.MapProjection overviewZoom = EarthquakeMapView.MapProjection.Create(
            [eventPolygon],
            [],
            [],
            EarthquakeMapFocusMode.SelectedEvent,
            null,
            new GeoCoordinate(32.75, 130.7),
            new MapGeometryBounds(130.4, 131.0, 32.4, 33.1),
            null,
            1,
            1000,
            600,
            0,
            0,
            overviewBounds);
        EarthquakeMapView.MapProjection nextZoom = EarthquakeMapView.MapProjection.Create(
            [eventPolygon],
            [],
            [],
            EarthquakeMapFocusMode.SelectedEvent,
            null,
            new GeoCoordinate(32.75, 130.7),
            new MapGeometryBounds(130.4, 131.0, 32.4, 33.1),
            null,
            2,
            1000,
            600,
            0,
            0,
            overviewBounds);

        double firstDistance =
            overviewZoom.Project(new GeoCoordinate(32.75, 130.71)).X -
            overviewZoom.Project(new GeoCoordinate(32.75, 130.7)).X;
        double nextDistance =
            nextZoom.Project(new GeoCoordinate(32.75, 130.71)).X -
            nextZoom.Project(new GeoCoordinate(32.75, 130.7)).X;

        Assert.Equal(1.25, nextDistance / firstDistance, precision: 6);
        Assert.Equal(1, EarthquakeMapView.MapProjection.ZoomLevelForScale(1), precision: 6);
        Assert.True(EarthquakeMapView.MapProjection.ZoomLevelForScale(10) > 1);
    }
}
