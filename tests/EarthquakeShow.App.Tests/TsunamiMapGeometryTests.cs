using System.Windows;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.App.Views;
using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class TsunamiMapGeometryTests
{
    [Fact]
    public void LoadFromJson_ReadsForecastAreaMultiLineAndBounds()
    {
        const string json = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "forecastAreaCode": "530", "name": "和歌山" },
                  "geometry": {
                    "type": "MultiLineString",
                    "coordinates": [
                      [[135.0, 33.0], [135.2, 33.1]],
                      [[135.3, 33.2], [135.4, 33.3]]
                    ]
                  }
                }
              ]
            }
            """;

        string path = WriteTemporaryFile(json);
        try
        {
            TsunamiMapGeometry geometry = TsunamiMapGeometry.LoadFromFile(path);

            Assert.Equal(2, geometry.Lines.Length);
            Assert.All(geometry.Lines, line => Assert.Equal("530", line.Code));
            Assert.Equal(135.0, geometry.Bounds.MinLongitude);
            Assert.Equal(135.4, geometry.Bounds.MaxLongitude);
            Assert.Equal(33.0, geometry.Bounds.MinLatitude);
            Assert.Equal(33.3, geometry.Bounds.MaxLatitude);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromJson_WithStrideKeepsLineEndpointsForOverview()
    {
        const string json = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "forecastAreaCode": "530", "name": "和歌山" },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [[135.0, 33.0], [135.1, 33.1], [135.2, 33.2], [135.3, 33.3], [135.4, 33.4]]
                  }
                }
              ]
            }
            """;

        string path = WriteTemporaryFile(json);
        try
        {
            TsunamiMapGeometry geometry = TsunamiMapGeometry.LoadFromFile(path, pointStride: 2);

            Assert.Equal(3, geometry.Lines[0].Coordinates.Length);
            Assert.Equal(new GeoCoordinate(33.0, 135.0), geometry.Lines[0].Coordinates[0]);
            Assert.Equal(new GeoCoordinate(33.4, 135.4), geometry.Lines[0].Coordinates[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetStationCenteringOffset_PlacesProjectedPointAtViewportCenter()
    {
        Vector offset = TsunamiMapView.GetStationCenteringOffset(
            new Point(360, 180),
            zoomLevel: 2,
            width: 400,
            height: 300);

        Assert.Equal(-320, offset.X);
        Assert.Equal(-60, offset.Y);
    }

    [Fact]
    public void LineSimplification_KeepsEndpointsAndDropsSubpixelPoints()
    {
        Assert.False(TsunamiMapView.ShouldKeepLinePoint(
            new Point(0, 0),
            new Point(0.2, 0.1),
            isLastPoint: false,
            minimumPixelDistance: 0.65));
        Assert.True(TsunamiMapView.ShouldKeepLinePoint(
            new Point(0, 0),
            new Point(0.2, 0.1),
            isLastPoint: true,
            minimumPixelDistance: 0.65));
        Assert.Equal(
            TsunamiMapView.DenseLineSimplificationPixels,
            TsunamiMapView.GetLineSimplificationPixels(TsunamiMapView.DenseLinePointThreshold));
    }

    [Fact]
    public void WheelPreviewScale_IsStableForFiniteZoomDelta()
    {
        Assert.Equal(1.25, TsunamiMapView.GetWheelPreviewScale(1, 2), precision: 6);
        Assert.Equal(1, TsunamiMapView.GetWheelPreviewScale(double.NaN, 2));
    }

    [Fact]
    public void WheelPreviewTranslationAccountsForFormalScaleAndPan()
    {
        Assert.Equal(
            new Vector(-100, -70),
            TsunamiMapView.GetWheelPreviewTranslation(
                new Point(100, 80),
                new Vector(0, 0),
                baseZoomLevel: 2,
                previewScale: 0.5,
                width: 400,
                height: 300));
    }

    [Fact]
    public void ComposePanOffset_PreservesAutomaticAndManualTranslation()
    {
        Assert.Equal(
            new Vector(-15, 28),
            TsunamiMapView.ComposePanOffset(new Vector(-40, 10), new Vector(25, 18)));
    }

    [Theory]
    [InlineData(1, false, false, 12)]
    [InlineData(8, true, true, 3.75)]
    public void ObservationMarkerSizeRemainsScreenSizedAcrossZoom(
        double zoomLevel,
        bool isSelected,
        bool showLabel,
        double expected)
    {
        Assert.Equal(
            expected,
            TsunamiMapView.GetObservationMarkerSize(zoomLevel, isSelected, showLabel),
            precision: 6);
    }

    [Theory]
    [InlineData(7.99, false)]
    [InlineData(8, true)]
    public void ObservationLabelsAppearOnlyAtHighZoom(double zoomLevel, bool expected)
    {
        Assert.Equal(expected, TsunamiMapView.ShouldShowObservationLabels(zoomLevel));
    }

    [Fact]
    public void GeometryJump_IsolatedWhenProjectedSegmentIsTooLong()
    {
        Assert.True(TsunamiMapView.IsGeometryJump(new Point(0, 0), new Point(501, 0)));
        Assert.False(TsunamiMapView.IsGeometryJump(new Point(0, 0), new Point(499, 0)));
    }

    [Theory]
    [InlineData(TsunamiLevel.MinorChange, 1)]
    [InlineData(TsunamiLevel.Advisory, 2)]
    [InlineData(TsunamiLevel.Warning, 3)]
    [InlineData(TsunamiLevel.MajorWarning, 4)]
    public void LegendLevels_AreContinuousUpToHighestLevel(TsunamiLevel highest, int count)
    {
        TsunamiLevel[] result = TsunamiMapView.BuildLegendLevels([highest]);

        Assert.Equal(count, result.Length);
        Assert.Equal("海啸预报", TsunamiMapView.GetTsunamiLegendText(result[0]));
        Assert.Equal("大海啸警报", TsunamiMapView.GetTsunamiLegendText(TsunamiLevel.MajorWarning));
    }

    [Fact]
    public void PackagedTsunamiLodResources_IncreaseGeometryDetail()
    {
        string mapDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Data",
            "Map");

        TsunamiMapGeometry low = TsunamiMapGeometry.LoadFromFile(
            Path.Combine(mapDirectory, "jma-tsunami-forecast-lines-low.geojson"));
        TsunamiMapGeometry medium = TsunamiMapGeometry.LoadFromFile(
            Path.Combine(mapDirectory, "jma-tsunami-forecast-lines-medium.geojson"));
        TsunamiMapGeometry detailed = TsunamiMapGeometry.LoadFromFile(
            Path.Combine(mapDirectory, "jma-tsunami-forecast-lines-overview.geojson"));

        int CountPoints(TsunamiMapGeometry geometry) =>
            geometry.Lines.Sum(line => line.Coordinates.Length);

        Assert.True(CountPoints(low) < CountPoints(medium));
        Assert.True(CountPoints(medium) < CountPoints(detailed));
        Assert.Equal(low.Lines.Select(line => line.Code).OrderBy(code => code),
            medium.Lines.Select(line => line.Code).OrderBy(code => code));
    }

    private static string WriteTemporaryFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsunami-map-{Guid.NewGuid():N}.geojson");
        File.WriteAllText(path, content);
        return path;
    }
}
