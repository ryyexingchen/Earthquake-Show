using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaStationCoordinateCatalogTests
{
    private static readonly string RepositoryRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..");

    [Fact]
    public void LoadFile_FormalCatalog_LoadsAllStationsAndDiagnostics()
    {
        string path = Path.Combine(
            RepositoryRoot,
            "src",
            "EarthquakeShow.App",
            "Assets",
            "Data",
            "Stations",
            "jma-intensity-stations.json");

        JmaStationCoordinateCatalog catalog = JmaStationCoordinateCatalog.LoadFile(path);

        Assert.Equal(4368, catalog.Entries.Length);
        Assert.Equal(4368, catalog.Diagnostics.CoordinateCount);
        Assert.Equal(4368, catalog.Diagnostics.MissingCodeCount);
        Assert.Equal(0, catalog.Diagnostics.MissingCoordinateCount);
        Assert.Equal(0, catalog.Diagnostics.DuplicateNameCount);
        Assert.Equal("jma-intensity-stations-2026-08-19", catalog.DatasetVersion);
        Assert.Equal("2026-08-19", catalog.RetrievedDate);
        Assert.Equal("WGS84 longitude/latitude", catalog.CoordinateReferenceSystem);
        Assert.Equal(
            "source-does-not-provide-jmaxml-station-code",
            catalog.StationCodeStatus);
    }

    [Fact]
    public void TryResolve_CodeMatchTakesPriorityOverUniqueNormalizedName()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "stations": [
                { "name": "名称一致観測点", "latitude": 35.0, "longitude": 135.0 }
              ]
            }
            """;
        var codeCoordinates = new Dictionary<string, GeoCoordinate>
        {
            ["1234567"] = new GeoCoordinate(36.0, 136.0),
        };
        JmaStationCoordinateCatalog catalog =
            JmaStationCoordinateCatalog.LoadJson(json, codeCoordinates);

        bool found = catalog.TryResolve(
            "1234567",
            "名称一致観測点＊",
            out GeoCoordinate coordinate,
            out JmaStationCoordinateMatchKind matchKind);

        Assert.True(found);
        Assert.Equal(new GeoCoordinate(36.0, 136.0), coordinate);
        Assert.Equal(JmaStationCoordinateMatchKind.Code, matchKind);
    }

    [Fact]
    public void TryResolve_DuplicateNameDoesNotGuessCoordinateAndMissingCoordinateIsRetained()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "stations": [
                { "name": "重複観測点", "latitude": 35.0, "longitude": 135.0 },
                { "name": "重複観測点＊" },
                { "stationCode": "7654321", "name": "座標なし観測点" }
              ]
            }
            """;
        JmaStationCoordinateCatalog catalog = JmaStationCoordinateCatalog.LoadJson(json);

        bool found = catalog.TryResolve("unknown", "重複観測点", out _, out var matchKind);

        Assert.False(found);
        Assert.Equal(JmaStationCoordinateMatchKind.None, matchKind);
        Assert.Equal(3, catalog.Entries.Length);
        Assert.Equal(1, catalog.Diagnostics.DuplicateNameCount);
        Assert.Equal(2, catalog.Diagnostics.MissingCoordinateCount);
    }
}
