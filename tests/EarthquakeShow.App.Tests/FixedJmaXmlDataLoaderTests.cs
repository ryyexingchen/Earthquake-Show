using EarthquakeShow.App.Services;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class FixedJmaXmlDataLoaderTests
{
    [Fact]
    public void LoadStationCatalog_FixedCsvMissing_LoadsFormalResourceIndependently()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"earthquake-show-stations-{Guid.NewGuid():N}");
        string catalogDirectory = Path.Combine(root, "Data", "Stations");
        Directory.CreateDirectory(catalogDirectory);
        File.WriteAllText(
            Path.Combine(catalogDirectory, "jma-intensity-stations.json"),
            """
            {
              "schemaVersion": 1,
              "datasetVersion": "formal-test",
              "stations": [
                { "name": "测试观测点", "latitude": 35.1, "longitude": 135.2 }
              ]
            }
            """);

        try
        {
            JmaStationCoordinateCatalog catalog =
                FixedJmaXmlDataLoader.LoadStationCatalog(root);

            Assert.Equal("formal-test", catalog.DatasetVersion);
            Assert.Single(catalog.Entries);
            Assert.Equal(0, catalog.Diagnostics.SupplementalCodeCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadStationCatalog_InvalidFormalResource_FallsBackToFixedCodeCoordinates()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"earthquake-show-stations-{Guid.NewGuid():N}");
        string catalogDirectory = Path.Combine(root, "Data", "Stations");
        Directory.CreateDirectory(catalogDirectory);
        File.WriteAllText(
            Path.Combine(root, "JmaStations.csv"),
            "station_code,report_name,coordinate_name,prefecture_code,affiliation,latitude,longitude\n" +
            "1234567,测试观测点,测试观测点,01,JMA,35.1,135.2\n");
        File.WriteAllText(
            Path.Combine(catalogDirectory, "jma-intensity-stations.json"),
            "{ invalid json }");

        try
        {
            JmaStationCoordinateCatalog catalog =
                FixedJmaXmlDataLoader.LoadStationCatalog(root);

            Assert.Equal("fixed-csv-fallback", catalog.DatasetVersion);
            Assert.True(catalog.TryResolve(
                "1234567",
                "其他名称",
                out _,
                out JmaStationCoordinateMatchKind matchKind));
            Assert.Equal(JmaStationCoordinateMatchKind.Code, matchKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
