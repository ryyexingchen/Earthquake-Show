using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaTsunamiXmlParserTests
{
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
}
