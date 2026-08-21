using System.Collections.Immutable;
using System.IO;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.App.Services;

internal static class FixedJmaXmlDataLoader
{
    public static JmaStationCoordinateCatalog LoadStationCatalog()
    {
        return LoadStationCatalog(Path.Combine(AppContext.BaseDirectory, "Assets"));
    }

    internal static JmaStationCoordinateCatalog LoadStationCatalog(string assetsRoot)
    {
        string stationPath = Path.Combine(assetsRoot, "JmaStations.csv");
        string catalogPath = Path.Combine(
            assetsRoot,
            "Data",
            "Stations",
            "jma-intensity-stations.json");
        IReadOnlyDictionary<string, GeoCoordinate>? fixedCoordinates = null;
        try
        {
            fixedCoordinates = JmaStationCatalog.LoadFile(stationPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            // 正式目录可以独立运行，固定 CSV 只提供代码增强和回退。
        }

        try
        {
            return JmaStationCoordinateCatalog.LoadFile(catalogPath, fixedCoordinates);
        }
        catch (Exception exception) when (
            fixedCoordinates is not null &&
            exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return JmaStationCoordinateCatalog.FromCodeCoordinates(fixedCoordinates);
        }
    }

    public static ImmutableArray<EarthquakeReport> LoadReports(
        JmaStationCoordinateCatalog stationCatalog)
    {
        ArgumentNullException.ThrowIfNull(stationCatalog);
        string assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        string officialRoot = Path.Combine(assetsRoot, "JmaXml", "Official");
        string syntheticRoot = Path.Combine(assetsRoot, "JmaXml", "Synthetic");

        var fixtures = new[]
        {
            CreateFixture(officialRoot, "20260818221220_0_VXSE51_010000.xml", "VXSE51"),
            CreateFixture(officialRoot, "20260818221317_0_VXSE52_270000.xml", "VXSE52"),
            CreateFixture(officialRoot, "20260818221432_0_VXSE53_270000.xml", "VXSE53"),
            CreateFixture(syntheticRoot, "vxse53-correction.xml", "VXSE53"),
        };

        try
        {
            return JmaXmlParser.LoadFixtures(fixtures, stationCatalog);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidOperationException(
                "固定 JMAXML 样本加载失败，请确认应用资源已正确复制。",
                exception);
        }
    }

    private static JmaXmlFixture CreateFixture(
        string directory,
        string fileName,
        string reportCode)
    {
        string path = Path.Combine(directory, fileName);
        return new JmaXmlFixture(path, reportCode, fileName);
    }
}
