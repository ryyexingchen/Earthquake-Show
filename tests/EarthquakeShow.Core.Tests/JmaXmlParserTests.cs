using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using System.Xml;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaXmlParserTests
{
    private static readonly string TestDataRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "tests", "TestData");

    [Fact]
    public void Parse_OfficialVxse51_MapsIntensityReport()
    {
        EarthquakeReport report = LoadOfficial("20260818221220_0_VXSE51_010000.xml", "VXSE51");

        Assert.Equal("20260819071048", report.EventId);
        Assert.Equal(EarthquakeReportType.SeismicIntensity, report.ReportType);
        Assert.Equal(JmaIntensity.Three, report.MaxIntensity);
        Assert.Single(report.IntensityAreas);
        Assert.Null(report.Hypocenter?.Coordinate);
        Assert.Null(report.Serial);
    }

    [Fact]
    public void Parse_OfficialVxse52_MapsHypocenterAndTsunamiComment()
    {
        EarthquakeReport report = LoadOfficial("20260818221317_0_VXSE52_270000.xml", "VXSE52");

        Assert.Equal(EarthquakeReportType.Hypocenter, report.ReportType);
        Assert.Equal(new GeoCoordinate(32.6, 130.6), report.Hypocenter?.Coordinate);
        Assert.Equal(10, report.Hypocenter?.DepthKm);
        Assert.Equal(3.8, report.Magnitude?.Value);
        Assert.Contains("津波", report.TsunamiComment);
    }

    [Fact]
    public void Parse_OfficialVxse53_MapsStationsWithCatalogCoordinates()
    {
        var stations = JmaStationCatalog.LoadFile(Path.Combine(TestDataRoot, "JmaStations.csv"));
        EarthquakeReport report = LoadOfficial(
            "20260818221432_0_VXSE53_270000.xml",
            "VXSE53",
            stations);

        Assert.Equal(EarthquakeReportType.HypocenterAndIntensity, report.ReportType);
        Assert.Equal(4, report.IntensityAreas.Select(area => area.PrefectureCode).Distinct().Count());
        Assert.Equal(7, report.IntensityAreas.Length);
        Assert.Equal(41, report.IntensityMunicipalities.Length);
        Assert.Equal(75, report.IntensityStations.Length);
        Assert.All(report.IntensityStations, station => Assert.NotNull(station.Coordinate));
    }

    [Fact]
    public void Parse_CorrectionFixture_MapsStatusSerialAndMagnitude()
    {
        EarthquakeReport report = LoadFixture(
            Path.Combine(TestDataRoot, "JmaXml", "Synthetic", "vxse53-correction.xml"),
            "VXSE53",
            JmaStationCatalog.LoadFile(Path.Combine(TestDataRoot, "JmaStations.csv")));

        Assert.Equal(ReportStatus.Correction, report.Status);
        Assert.Equal(2, report.Serial);
        Assert.Equal(3.9, report.Magnitude?.Value);
    }

    [Fact]
    public void Parse_MissingFieldsFixture_PreservesPartialData()
    {
        EarthquakeReport report = LoadFixture(
            Path.Combine(TestDataRoot, "JmaXml", "Synthetic", "vxse53-missing-fields.xml"),
            "VXSE53");

        Assert.Null(report.Magnitude?.Value);
        Assert.Null(report.Hypocenter?.Coordinate);
        Assert.Equal(JmaIntensity.Four, report.MaxIntensity);
        IntensityStation station = Assert.Single(report.IntensityStations);
        Assert.Null(station.Coordinate);
    }

    [Fact]
    public void Parse_StationCodeMissingFromIndex_UsesUniqueNormalizedName()
    {
        const string catalogJson = """
            {
              "schemaVersion": 1,
              "stations": [
                { "name": "坐标不明观测点", "latitude": 35.1, "longitude": 135.2 }
              ]
            }
            """;
        string path = Path.Combine(
            TestDataRoot,
            "JmaXml",
            "Synthetic",
            "vxse53-missing-fields.xml");
        string xml = File.ReadAllText(path);

        EarthquakeReport report = JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions(
                "VXSE53",
                new SourceReference("jma-xml", "name-fallback"),
                StationCatalog: JmaStationCoordinateCatalog.LoadJson(catalogJson)));

        Assert.Equal(
            new GeoCoordinate(35.1, 135.2),
            Assert.Single(report.IntensityStations).Coordinate);
    }

    [Fact]
    public void LoadFixtures_OfficialSequence_MergesIntoOneEvent()
    {
        string officialRoot = Path.Combine(TestDataRoot, "JmaXml", "Official");
        var fixtures = new[]
        {
            new JmaXmlFixture(Path.Combine(officialRoot, "20260818221220_0_VXSE51_010000.xml"), "VXSE51", "official-vxse51"),
            new JmaXmlFixture(Path.Combine(officialRoot, "20260818221317_0_VXSE52_270000.xml"), "VXSE52", "official-vxse52"),
            new JmaXmlFixture(Path.Combine(officialRoot, "20260818221432_0_VXSE53_270000.xml"), "VXSE53", "official-vxse53"),
        };

        var events = EarthquakeEventMerger.Merge(
            JmaXmlParser.LoadFixtures(
                fixtures,
                JmaStationCatalog.LoadFile(Path.Combine(TestDataRoot, "JmaStations.csv"))));

        EarthquakeEvent earthquakeEvent = Assert.Single(events);
        Assert.Equal(3, earthquakeEvent.Reports.Length);
        Assert.Equal("VXSE53", earthquakeEvent.LatestReport?.ReportCode);
    }

    [Fact]
    public void Parse_DoctypesAreRejected()
    {
        const string xml = "<!DOCTYPE Report [<!ELEMENT Report ANY>]><Report><Head><EventID>event</EventID><ReportDateTime>2026-08-19T00:00:00Z</ReportDateTime><InfoType>発表</InfoType></Head><Control><DateTime>2026-08-19T00:00:00Z</DateTime><Status>通常</Status></Control></Report>";
        Assert.Throws<XmlException>(() => JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions("VXSE53", new SourceReference("jma-xml", "message"))));
    }

    [Fact]
    public void Parse_MissingEventIdIsRejected()
    {
        const string xml = "<Report><Head><ReportDateTime>2026-08-19T00:00:00Z</ReportDateTime></Head><Control><DateTime>2026-08-19T00:00:00Z</DateTime></Control></Report>";
        Assert.Throws<FormatException>(() => JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions("VXSE53", new SourceReference("jma-xml", "message"))));
    }

    [Fact]
    public void Parse_InvalidCoordinateIsRejected()
    {
        const string xml = "<Report><Head><EventID>event</EventID><ReportDateTime>2026-08-19T00:00:00Z</ReportDateTime><InfoType>発表</InfoType></Head><Control><DateTime>2026-08-19T00:00:00Z</DateTime><Status>通常</Status></Control><Body><Earthquake><Hypocenter><Area><jmx_eb:Coordinate xmlns:jmx_eb=\"http://xml.kishou.go.jp/jmaxml1/elementBasis1/\">invalid</jmx_eb:Coordinate></Area></Hypocenter></Earthquake></Body></Report>";
        Assert.Throws<FormatException>(() => JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions("VXSE53", new SourceReference("jma-xml", "message"))));
    }

    [Theory]
    [InlineData("", "message")]
    [InlineData("VXSE53", "")]
    public void Parse_InvalidOptionsAreRejected(string reportCode, string sourceMessageId)
    {
        const string xml = "<Report><Head><EventID>event</EventID><ReportDateTime>2026-08-19T00:00:00Z</ReportDateTime></Head><Control><DateTime>2026-08-19T00:00:00Z</DateTime></Control></Report>";
        Assert.Throws<ArgumentException>(() => JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions(reportCode, new SourceReference("jma-xml", sourceMessageId))));
    }

    private static EarthquakeReport LoadOfficial(
        string fileName,
        string reportCode,
        IReadOnlyDictionary<string, GeoCoordinate>? stations = null)
    {
        return LoadFixture(Path.Combine(TestDataRoot, "JmaXml", "Official", fileName), reportCode, stations);
    }

    private static EarthquakeReport LoadFixture(
        string path,
        string reportCode,
        IReadOnlyDictionary<string, GeoCoordinate>? stations = null)
    {
        string xml = File.ReadAllText(path);
        return JmaXmlParser.Parse(
            xml,
            new JmaXmlParseOptions(
                reportCode,
                new SourceReference("jma-xml", Path.GetFileName(path)),
                StationCoordinates: stations));
    }
}
