using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class EarthquakeEventMergerTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 19, 7, 10, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Merge_VxseReportSequence_CreatesOneEventTimeline()
    {
        EarthquakeReport vxse51 = CreateReport("VXSE51", "message-51", 1);
        EarthquakeReport vxse52 = CreateReport("VXSE52", "message-52", 2);
        EarthquakeReport vxse53 = CreateReport("VXSE53", "message-53", 3);

        var result = EarthquakeEventMerger.Merge([vxse51, vxse52, vxse53]);

        EarthquakeEvent earthquakeEvent = Assert.Single(result);
        Assert.Equal(["VXSE51", "VXSE52", "VXSE53"],
            earthquakeEvent.Reports.Select(report => report.ReportCode));
        Assert.Equal(vxse53, earthquakeEvent.LatestReport);
    }

    [Fact]
    public void Merge_ShuffledInput_ProducesSameTimeline()
    {
        EarthquakeReport first = CreateReport("VXSE51", "message-51", 1);
        EarthquakeReport second = CreateReport("VXSE52", "message-52", 2);
        EarthquakeReport third = CreateReport("VXSE53", "message-53", 3);

        var chronological = EarthquakeEventMerger.Merge([first, second, third]);
        var shuffled = EarthquakeEventMerger.Merge([third, first, second]);

        Assert.Equal(
            chronological[0].Reports.Select(report => report.Source.SourceMessageId),
            shuffled[0].Reports.Select(report => report.Source.SourceMessageId));
        Assert.Equal(chronological[0].Summary, shuffled[0].Summary);
    }

    [Fact]
    public void Merge_DuplicateSourceMessage_CreatesOneTimelineNode()
    {
        EarthquakeReport report = CreateReport("VXSE53", "message-53", 3);

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([report, report]));

        Assert.Single(earthquakeEvent.Reports);
    }

    [Fact]
    public void Merge_DuplicateSourceMessage_KeepsEarliestReceivedReport()
    {
        EarthquakeReport laterReceived = CreateReport(
            "VXSE53",
            "message-53",
            3,
            receivedSecond: 30,
            magnitude: 4.1);
        EarthquakeReport earlierReceived = CreateReport(
            "VXSE53",
            "message-53",
            3,
            receivedSecond: 10,
            magnitude: 3.8);

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([laterReceived, earlierReceived]));

        EarthquakeReport keptReport = Assert.Single(earthquakeEvent.Reports);
        Assert.Equal(earlierReceived, keptReport);
        Assert.Equal(3.8, earthquakeEvent.Summary?.Magnitude?.Value);
    }

    [Fact]
    public void Merge_EqualIssueTime_UsesAllStableSortKeys()
    {
        EarthquakeReport nullSerial = CreateReport(
            "NULL", "message-null", 1, serial: null, receivedSecond: 50);
        EarthquakeReport earlyReceived = CreateReport(
            "EARLY", "message-early", 1, serial: 1, receivedSecond: 10);
        EarthquakeReport sourceFirst = CreateReport(
            "SOURCE-A", "message-z", 1, serial: 1, receivedSecond: 20, sourceId: "a");
        EarthquakeReport messageFirst = CreateReport(
            "MESSAGE-A", "message-a", 1, serial: 1, receivedSecond: 20, sourceId: "b");
        EarthquakeReport messageLast = CreateReport(
            "MESSAGE-Z", "message-z", 1, serial: 1, receivedSecond: 20, sourceId: "b");

        EarthquakeEvent earthquakeEvent = Assert.Single(EarthquakeEventMerger.Merge(
            [messageLast, messageFirst, sourceFirst, earlyReceived, nullSerial]));

        Assert.Equal(
            ["NULL", "EARLY", "SOURCE-A", "MESSAGE-A", "MESSAGE-Z"],
            earthquakeEvent.Reports.Select(report => report.ReportCode));
    }

    [Fact]
    public void Merge_EqualReportIdentity_PrefersJmaXmlOverJson()
    {
        EarthquakeReport json = CreateReport(
            "JMA-JSON",
            "json-message",
            1,
            sourceId: "jma-json");
        EarthquakeReport xml = CreateReport(
            "VXSE53",
            "xml-message",
            1,
            sourceId: "jma-xml");

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([json, xml]));

        Assert.Equal("jma-xml", earthquakeEvent.LatestReport?.Source.SourceId);
        Assert.Equal("VXSE53", earthquakeEvent.LatestReport?.ReportCode);
    }

    [Fact]
    public void Merge_MultipleEvents_OrdersNewestEventFirst()
    {
        EarthquakeReport older = CreateReport(
            "VXSE53", "older", 1, eventId: "event-b");
        EarthquakeReport newerA = CreateReport(
            "VXSE53", "newer-a", 3, eventId: "event-a");
        EarthquakeReport newerC = CreateReport(
            "VXSE53", "newer-c", 3, eventId: "event-c");

        var events = EarthquakeEventMerger.Merge([older, newerC, newerA]);

        Assert.Equal(["event-a", "event-c", "event-b"],
            events.Select(earthquakeEvent => earthquakeEvent.EventId));
    }

    [Fact]
    public void Merge_OlderReport_DoesNotReplaceLatestSummary()
    {
        EarthquakeReport newer = CreateReport(
            "VXSE53", "newer", 3, magnitude: 4.2, intensity: JmaIntensity.Four);
        EarthquakeReport older = CreateReport(
            "VXSE52", "older", 1, magnitude: 3.1, intensity: JmaIntensity.Two);

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([newer, older]));

        Assert.Equal("VXSE53", earthquakeEvent.Summary?.ReportCode);
        Assert.Equal(4.2, earthquakeEvent.Summary?.Magnitude?.Value);
        Assert.Equal(JmaIntensity.Four, earthquakeEvent.Summary?.MaxIntensity);
    }

    [Fact]
    public void Merge_Correction_UpdatesSummaryAndKeepsOriginalReport()
    {
        EarthquakeReport original = CreateReport(
            "VXSE53", "original", 1, serial: 1, magnitude: 3.8);
        EarthquakeReport correction = CreateReport(
            "VXSE53", "correction", 2, serial: 2,
            status: ReportStatus.Correction, magnitude: 4.0);

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([correction, original]));

        Assert.Equal(2, earthquakeEvent.Reports.Length);
        Assert.Equal(original, earthquakeEvent.Reports[0]);
        Assert.Equal(correction, earthquakeEvent.LatestEffectiveReport);
        Assert.Equal(ReportStatus.Correction, earthquakeEvent.Summary?.Status);
        Assert.Equal(4.0, earthquakeEvent.Summary?.Magnitude?.Value);
    }

    [Fact]
    public void Merge_Cancellation_SetsCurrentStateAndKeepsLastEffectiveSummary()
    {
        EarthquakeReport issued = CreateReport(
            "VXSE53", "issued", 1, magnitude: 3.8, intensity: JmaIntensity.Three);
        EarthquakeReport cancelled = CreateReport(
            "VXSE53", "cancelled", 2, status: ReportStatus.Cancelled,
            magnitude: null, intensity: JmaIntensity.Unknown);

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([cancelled, issued]));

        Assert.Equal(cancelled, earthquakeEvent.LatestReport);
        Assert.Equal(issued, earthquakeEvent.LatestEffectiveReport);
        Assert.Equal(ReportStatus.Cancelled, earthquakeEvent.Summary?.Status);
        Assert.Equal(cancelled.IssuedAt, earthquakeEvent.Summary?.UpdatedAt);
        Assert.Equal(3.8, earthquakeEvent.Summary?.Magnitude?.Value);
        Assert.Equal(JmaIntensity.Three, earthquakeEvent.Summary?.MaxIntensity);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmptyCollection()
    {
        Assert.Empty(EarthquakeEventMerger.Merge([]));
    }

    [Theory]
    [InlineData("", "jma-xml", "message")]
    [InlineData("event", "", "message")]
    [InlineData("event", "jma-xml", "")]
    [InlineData(" ", "jma-xml", "message")]
    public void Merge_InvalidIdentity_IsRejected(
        string eventId,
        string sourceId,
        string sourceMessageId)
    {
        EarthquakeReport report = CreateReport(
            "VXSE53",
            sourceMessageId,
            1,
            eventId: eventId,
            sourceId: sourceId);

        Assert.Throws<ArgumentException>(() => EarthquakeEventMerger.Merge([report]));
    }

    private static EarthquakeReport CreateReport(
        string reportCode,
        string sourceMessageId,
        int issuedMinute,
        int? serial = 1,
        int receivedSecond = 1,
        ReportStatus status = ReportStatus.Issued,
        double? magnitude = 3.8,
        JmaIntensity intensity = JmaIntensity.Three,
        string eventId = "20260819071048",
        string sourceId = "jma-xml")
    {
        DateTimeOffset issuedAt = BaseTime.AddMinutes(issuedMinute);
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = reportCode,
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            Status = status,
            Context = ReportContext.Normal,
            Serial = serial,
            OriginTime = BaseTime,
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(receivedSecond),
            Hypocenter = new Hypocenter(
                "熊本県天草・芦北地方",
                "743",
                new GeoCoordinate(32.6, 130.6),
                10),
            Magnitude = magnitude is null ? null : new Magnitude(magnitude, "Mj"),
            MaxIntensity = intensity,
            Source = new SourceReference(sourceId, sourceMessageId),
        };
    }
}
