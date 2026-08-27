using EarthquakeShow.App.ViewModels;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class MapBoundaryGeometryTests
{
    [Fact]
    public void LoadFromJson_ReadsLinesMetadataAndAreaIndex()
    {
        OfflineMapBoundaryGeometry geometry = OfflineMapBoundaryGeometry.LoadFromJson(ValidJson);

        Assert.Equal(4, geometry.Boundaries.Length);
        Assert.Equal(2, geometry.GetForArea("A").Count);
        Assert.Equal(3, geometry.GetForArea("B").Count);
        Assert.Equal(2, geometry.GetForArea(" C ").Count);
        Assert.Empty(geometry.GetForArea("missing"));
        Assert.Equal("JMA GIS", geometry.Source);
        Assert.Equal("20240520", geometry.SourceVersion);
        Assert.True(geometry.IsOfficialBoundary);
        Assert.Equal(7, geometry.TopologyPrecision);
        Assert.Equal(0.015, geometry.SimplificationToleranceDegrees, precision: 3);
        Assert.Equal(129, geometry.Bounds.MinLongitude, precision: 3);
        Assert.Equal(131, geometry.Bounds.MaxLongitude, precision: 3);
        Assert.Equal(129, geometry.Boundaries[0].Bounds.MinLongitude, precision: 3);
        Assert.Equal(130, geometry.Boundaries[0].Bounds.MaxLongitude, precision: 3);
        Assert.Equal(30, geometry.Boundaries[0].Bounds.MinLatitude, precision: 3);
    }

    [Fact]
    public void LoadFromJson_ReportsInvalidFeaturesAndSkipsThem()
    {
        const string json = """
            {
              "type":"FeatureCollection",
              "features":[
                {"properties":{"areaCode1":"A","areaCode2":""},"geometry":{"type":"LineString","coordinates":[[130,30],[131,30]]}},
                {"properties":{"areaCode1":"","areaCode2":"B"},"geometry":{"type":"LineString","coordinates":[[130,31],[131,31]]}},
                {"properties":{"areaCode1":"B","areaCode2":"C"},"geometry":{"type":"LineString","coordinates":[[130,32]]}}
              ]
            }
            """;

        OfflineMapBoundaryGeometry geometry = OfflineMapBoundaryGeometry.LoadFromJson(json);

        Assert.Single(geometry.Boundaries);
        Assert.Equal(2, geometry.InvalidGeometryCount);
        Assert.Single(geometry.GetForArea("A"));
        Assert.Empty(geometry.GetForArea("B"));
    }

    [Fact]
    public void LoadFromJson_RejectsPolygonGeometry()
    {
        const string json = """
            {"type":"FeatureCollection","features":[{"properties":{"areaCode1":"A","areaCode2":""},"geometry":{"type":"Polygon","coordinates":[]}}]}
            """;

        Assert.Throws<FormatException>(() => OfflineMapBoundaryGeometry.LoadFromJson(json));
    }

    [Fact]
    public void LoadFromJson_FiltersLinesOutsideViewportBounds()
    {
        OfflineMapBoundaryGeometry geometry = OfflineMapBoundaryGeometry.LoadFromJson(
            ValidJson,
            new MapGeometryBounds(129.5, 130.5, 29.5, 30.5));

        Assert.Equal(2, geometry.Boundaries.Length);
        Assert.Single(geometry.GetForArea("A"));
        Assert.Equal(2, geometry.GetForArea("B").Count);
    }

    [Fact]
    public void FromPolygons_CreatesIndexedHighDetailOutlines()
    {
        OfflineMapGeometry geometry = OfflineMapGeometry.LoadFromJson(
            """
            {"type":"FeatureCollection","metadata":{"source":"高精度区域"},"features":[{"type":"Feature","properties":{"areaCode":"100","name":"区域"},"geometry":{"type":"Polygon","coordinates":[[[130,32],[131,32],[131,33],[130,33],[130,32]]]}}]}
            """);

        OfflineMapBoundaryGeometry boundaries =
            OfflineMapBoundaryGeometry.FromPolygons(geometry);

        Assert.Single(boundaries.Boundaries);
        Assert.Single(boundaries.GetForArea("100"));
        Assert.Contains("区域轮廓", boundaries.Source);
    }

    private const string ValidJson = """
        {
          "type":"FeatureCollection",
          "metadata":{
            "source":"JMA GIS",
            "sourceVersion":"20240520",
            "officialBoundary":true,
            "topologyPrecision":7,
            "simplificationToleranceDegrees":0.015,
            "minRingAreaDegreesSquared":0.0002
          },
          "features":[
            {"properties":{"areaCode1":"A","areaCode2":"B"},"geometry":{"type":"LineString","coordinates":[[129,30],[130,30]]}},
            {"properties":{"areaCode1":"A","areaCode2":""},"geometry":{"type":"LineString","coordinates":[[129,31],[130,31]]}},
            {"properties":{"areaCode1":"B","areaCode2":"C"},"geometry":{"type":"MultiLineString","coordinates":[[[130,30],[131,30]],[[130,31],[131,31]]]}}
          ]
        }
        """;
}
