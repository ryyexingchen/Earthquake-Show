using System.Windows;
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
