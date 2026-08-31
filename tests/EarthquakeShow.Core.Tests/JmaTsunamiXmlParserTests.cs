using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaTsunamiXmlParserTests
{
    [Fact]
    public async Task Parse_OfficialVtse41_ReadsForecastArrivalAndHeightDescription()
    {
        string xml = await ReadOfficialAsync("32-39_12_02_250206_VTSE41.xml");

        JmaTsunamiReport report = JmaTsunamiXmlParser.Parse(
            xml,
            new JmaTsunamiXmlParseOptions(
                "VTSE41",
                new SourceReference("jma-xml-tsunami", "vtse41")));

        Assert.Equal("20160901071000", report.EventId);
        Assert.Equal(ReportContext.Training, report.Context);
        Assert.Equal(new DateTimeOffset(2016, 9, 1, 7, 10, 0, TimeSpan.FromHours(9)), report.OriginTime);
        Assert.Equal("和歌山県南方沖", report.Hypocenter?.Name);
        Assert.Equal(10, report.Hypocenter?.DepthKm);
        Assert.Null(report.Magnitude?.Value);
        Assert.Equal("Ｍ８を超える巨大地震", report.Magnitude?.Description);
        Assert.NotEmpty(report.ForecastAreas);
        JmaTsunamiForecastArea area = Assert.Single(
            report.ForecastAreas,
            item => item.Code == "300");
        Assert.Equal("大津波警報：発表", area.KindName);
        Assert.Equal("53", area.KindCode);
        Assert.Equal(new DateTimeOffset(2016, 9, 1, 8, 10, 0, TimeSpan.FromHours(9)), area.FirstArrivalTime);
        Assert.Null(area.MaximumHeight!.Meters);
        Assert.Equal("巨大", area.MaximumHeight.Description);
    }

    [Fact]
    public async Task Parse_OfficialVtse51_ReadsCoastalForecastStations()
    {
        string xml = await ReadOfficialAsync("32-39_12_03_250206_VTSE51.xml");

        JmaTsunamiReport report = JmaTsunamiXmlParser.Parse(
            xml,
            new JmaTsunamiXmlParseOptions(
                "VTSE51",
                new SourceReference("jma-xml-tsunami", "vtse51")));

        Assert.NotEmpty(report.ForecastAreas);
        JmaTsunamiForecastArea area = Assert.Single(
            report.ForecastAreas,
            item => item.Code == "300");
        JmaTsunamiStationForecast station = Assert.Single(
            area.Stations,
            item => item.Code == "30001");
        Assert.Equal("大洗", station.Name);
        Assert.Equal(new DateTimeOffset(2016, 9, 1, 16, 28, 0, TimeSpan.FromHours(9)), station.HighTideTime);
        Assert.Equal(new DateTimeOffset(2016, 9, 1, 8, 10, 0, TimeSpan.FromHours(9)), station.FirstArrivalTime);
    }

    [Fact]
    public async Task Parse_OfficialVtse52_ReadsObservationAndEstimation()
    {
        string xml = await ReadOfficialAsync("32-39_12_05_250206_VTSE52.xml");

        JmaTsunamiReport report = JmaTsunamiXmlParser.Parse(
            xml,
            new JmaTsunamiXmlParseOptions(
                "VTSE52",
                new SourceReference("jma-xml-tsunami", "vtse52")));

        JmaTsunamiObservationStation station = Assert.Single(
            report.ObservationStations,
            item => item.Code == "38090");
        Assert.Equal("ＧＰＳ波浪計", station.Sensor);
        Assert.Equal(new DateTimeOffset(2016, 9, 1, 7, 15, 0, TimeSpan.FromHours(9)), station.MaximumHeightTime);
        Assert.Equal(1.8, station.MaximumHeight!.Meters);
        Assert.NotEmpty(report.EstimationAreas);
        Assert.Contains(report.EstimationAreas, item => item.FirstArrivalTime is not null);
        Assert.Contains(
            "沖合での観測値であり、沿岸では津波はさらに高くなります。",
            report.FixedAdditionalTexts);
    }

    [Fact]
    public void Parse_HypocenterUnknown_UsesDescriptionFallback()
    {
        const string xml = """
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
              <Control><DateTime>2026-08-24T00:00:00Z</DateTime><Status>通常</Status></Control>
              <Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/">
                <ReportDateTime>2026-08-24T09:00:00+09:00</ReportDateTime><EventID>unknown-source</EventID>
                <InfoType>発表</InfoType><InfoKind>津波警報・注意報・予報</InfoKind>
                <Headline><Text>津波情報</Text></Headline>
              </Head>
              <Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/tsunami1/">
                <Earthquake><Hypocenter><Area><Name>不明</Name><Description>遠地地震のため震源不明</Description></Area></Hypocenter></Earthquake>
              </Body>
            </Report>
            """;

        JmaTsunamiReport report = JmaTsunamiXmlParser.Parse(
            xml,
            new JmaTsunamiXmlParseOptions(
                "VTSE41",
                new SourceReference("jma-xml-tsunami", "unknown-source")));

        Assert.Equal("不明", report.Hypocenter?.Name);
        Assert.Equal("遠地地震のため震源不明", report.Hypocenter?.Description);
    }

    [Fact]
    public void Parse_HeadlineItems_PreservesCurrentLastKindsAndAreas()
    {
        const string xml = """
            <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
              <Control><DateTime>2026-08-24T00:00:00Z</DateTime><Status>通常</Status></Control>
              <Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/">
                <ReportDateTime>2026-08-24T09:00:00+09:00</ReportDateTime><EventID>tsunami-event</EventID>
                <InfoType>発表</InfoType><Serial>3</Serial><InfoKind>津波警報・注意報・予報</InfoKind>
                <Headline><Text>津波注意報を発表しています。</Text><Information type="津波予報領域表現"><Item>
                  <Kind><Name>津波注意報</Name><Code>advisory-code</Code></Kind>
                  <LastKind><Name>津波予報</Name><Code>forecast-code</Code></LastKind>
                  <Areas><Area><Name>茨城県</Name><Code>301</Code></Area></Areas>
                </Item></Information></Headline>
              </Head>
              <Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/tsunami1/" />
            </Report>
            """;

        JmaTsunamiReport report = JmaTsunamiXmlParser.Parse(
            xml,
            new JmaTsunamiXmlParseOptions(
                "VTSE41",
                new SourceReference("jma-xml-tsunami", "tsunami-message")));

        Assert.Equal("tsunami-event", report.EventId);
        Assert.Equal(ReportStatus.Issued, report.Status);
        Assert.Equal(3, report.Serial);
        JmaTsunamiInformationItem item = Assert.Single(report.Items);
        Assert.Equal("津波注意報", item.KindName);
        Assert.Equal("advisory-code", item.KindCode);
        Assert.Equal("津波予報", item.LastKindName);
        Assert.Equal("forecast-code", item.LastKindCode);
        Assert.Equal(new JmaTsunamiArea("茨城県", "301"), Assert.Single(item.Areas));
    }

    [Fact]
    public void Parse_RejectsNonVtseReportCode()
    {
        Assert.Throws<ArgumentException>(() => JmaTsunamiXmlParser.Parse(
            "<Report />",
            new JmaTsunamiXmlParseOptions(
                "VXSE53",
                new SourceReference("jma-xml-tsunami", "invalid"))));
    }

    private static Task<string> ReadOfficialAsync(string fileName)
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "TestData", "JmaTsunami", "Official");
        return File.ReadAllTextAsync(Path.Combine(root, fileName));
    }
}
