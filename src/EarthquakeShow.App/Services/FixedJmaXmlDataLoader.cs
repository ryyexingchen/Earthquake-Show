using System.Collections.Immutable;
using System.IO;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;

namespace EarthquakeShow.App.Services;

internal static class FixedJmaXmlDataLoader
{
    public static ImmutableArray<EarthquakeReport> LoadReports()
    {
        string assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        string stationPath = Path.Combine(assetsRoot, "JmaStations.csv");
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
            var stations = JmaStationCatalog.LoadFile(stationPath);
            return JmaXmlParser.LoadFixtures(fixtures, stations);
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
