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
