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
    public void PendingViewportCenterIsNotOverwrittenByConsecutiveGeometryChanges()
    {
        GeoCoordinate firstCenter = new(35.25, 139.75);
        GeoCoordinate laterCenter = new(36.5, 140.5);

        GeoCoordinate preserved = EarthquakeMapView.PreservePendingViewportCenter(
            firstCenter,
            laterCenter);

        Assert.Equal(firstCenter, preserved);
        Assert.Equal(
            laterCenter,
            EarthquakeMapView.PreservePendingViewportCenter(null, laterCenter));
    }
}
