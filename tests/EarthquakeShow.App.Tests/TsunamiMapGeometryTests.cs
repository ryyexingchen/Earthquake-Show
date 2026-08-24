using EarthquakeShow.App.ViewModels;
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

    private static string WriteTemporaryFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsunami-map-{Guid.NewGuid():N}.geojson");
        File.WriteAllText(path, content);
        return path;
    }
}
