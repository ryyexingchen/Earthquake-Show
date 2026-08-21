using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaQuakeDetailJsonParserTests
{
    private static readonly string OfficialRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "tests", "TestData", "JmaJson", "Official");

    [Fact]
    public void Parse_OfficialVxse51_MapsExpandedIntensityAreas()
    {
        EarthquakeReport report = LoadOfficial(
            "20260728163103_20260728162718_VXSE51_0.json",
            "VXSE51");

        Assert.Equal("20260728162718", report.EventId);
        Assert.Equal(EarthquakeReportType.SeismicIntensity, report.ReportType);
        Assert.Equal(JmaIntensity.Seven, report.MaxIntensity);
        Assert.Equal(41, report.IntensityAreas.Length);
        Assert.Empty(report.IntensityMunicipalities);
        Assert.Empty(report.IntensityStations);
    }

    [Fact]
    public void Parse_OfficialVxse5kSecondReport_MapsCompleteObservationTree()
    {
        EarthquakeReport report = LoadOfficial(
            "20260728163528_20260728162718_VXSE5k_2.json",
            "VXSE53");

        Assert.Equal(EarthquakeReportType.HypocenterAndIntensity, report.ReportType);
        Assert.Equal(2, report.Serial);
        Assert.Equal("熊本県熊本地方", report.Hypocenter?.Name);
        Assert.Equal(new GeoCoordinate(32.6, 130.7), report.Hypocenter?.Coordinate);
        Assert.Equal(10, report.Hypocenter?.DepthKm);
        Assert.Equal(7.1, report.Magnitude?.Value);
        Assert.Equal(JmaIntensity.Seven, report.MaxIntensity);
        Assert.Equal(72, report.IntensityAreas.Length);
        Assert.Equal(555, report.IntensityMunicipalities.Length);
        Assert.Equal(1248, report.IntensityStations.Length);
        Assert.All(report.IntensityStations, station => Assert.NotNull(station.Coordinate));
        Assert.Contains("津波警報", report.TsunamiComment);
    }

    [Fact]
    public void Parse_OfficialVxse61_PrefersPreciseWgsCoordinate()
    {
        EarthquakeReport report = LoadOfficial(
            "20260728203023_20260728162718_VXSE61_0.json",
            "VXSE61");

        Assert.Equal(EarthquakeReportType.Hypocenter, report.ReportType);
        Assert.Equal(16, report.Hypocenter?.DepthKm);
        Assert.Equal(32.625, report.Hypocenter!.Coordinate!.Value.Latitude, 6);
        Assert.Equal(130.678333, report.Hypocenter.Coordinate.Value.Longitude, 6);
        Assert.Equal(7.1, report.Magnitude?.Value);
        Assert.Equal(JmaIntensity.Unknown, report.MaxIntensity);
    }

    private static EarthquakeReport LoadOfficial(string fileName, string reportCode)
    {
        string path = Path.Combine(OfficialRoot, fileName);
        string payload = File.ReadAllText(path);
        return JmaQuakeDetailJsonParser.Parse(
            payload,
            new JmaQuakeDetailJsonParseOptions(
                reportCode,
                new SourceReference(
                    "jma-json-detail",
                    fileName,
                    new Uri($"https://www.jma.go.jp/bosai/quake/data/{fileName}"))));
    }
}
