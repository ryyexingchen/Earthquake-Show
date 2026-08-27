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
    public void StationMarkerText_UnknownIntensityIsHidden()
    {
        Assert.Null(EarthquakeMapView.GetStationMarkerText(JmaIntensity.Unknown));
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
