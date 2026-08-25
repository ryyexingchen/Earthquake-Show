using System.Collections.Immutable;
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
            sourceId: "other");
        EarthquakeReport xml = CreateReport(
            "VXSE53",
            "xml-message",
            1,
            sourceId: "jma-xml");
        EarthquakeReport p2p = CreateReport(
            "P2P-551",
            "p2p-message",
            1,
            sourceId: "p2pquake");

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([json, xml, p2p]));

        Assert.Equal(
            ["other", "p2pquake", "jma-xml"],
            earthquakeEvent.Reports.Select(report => report.Source.SourceId));
        Assert.Equal("jma-xml", earthquakeEvent.LatestReport?.Source.SourceId);
        Assert.Equal("VXSE53", earthquakeEvent.LatestReport?.ReportCode);
    }

    [Fact]
    public void Merge_PreferredReport_UsesXmlWhenP2pIsNewer()
    {
        EarthquakeReport xml = CreateReport(
            "VXSE53",
            "xml-message",
            1,
            sourceId: "jma-xml");
        EarthquakeReport p2p = CreateReport(
            "P2P-551",
            "p2p-message",
            3,
            sourceId: "p2pquake");

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([xml, p2p]));

        Assert.Equal("p2pquake", earthquakeEvent.LatestReport?.Source.SourceId);
        Assert.Equal("jma-xml", earthquakeEvent.PreferredReport?.Source.SourceId);
    }

    [Fact]
    public void Merge_JmaTemporaryEventIds_Vxse51AndVxse52CreateOneEvent()
    {
        EarthquakeReport intensity = CreateReport(
            "VXSE51",
            "temporary-51",
            1,
            eventId: "20260824040519");
        EarthquakeReport hypocenter = CreateReport(
            "VXSE52",
            "temporary-52",
            2,
            eventId: "20260824040526");

        ImmutableArray<EarthquakeEvent> result = EarthquakeEventMerger.Merge(
            [intensity, hypocenter]);

        EarthquakeEvent earthquakeEvent = Assert.Single(result);
        Assert.Equal("20260824040526", earthquakeEvent.EventId);
        Assert.Equal(["VXSE51", "VXSE52"],
            earthquakeEvent.Reports.Select(report => report.ReportCode));
    }

    [Fact]
    public void Merge_P2pAndJmaXmlMatchingEvent_CreatesOneEvent()
    {
        EarthquakeReport jma = CreateReport(
            "VXSE53",
            "jma-message",
            2,
            eventId: "jma-event",
            sourceId: "jma-xml");
        EarthquakeReport p2p = CreateReport(
            "P2P-551",
            "p2p-message",
            1,
            eventId: "p2pquake:p2p-message",
            sourceId: "p2pquake");

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([p2p, jma]));

        Assert.Equal("jma-event", earthquakeEvent.EventId);
        Assert.Equal(2, earthquakeEvent.Reports.Length);
        Assert.Equal(["p2pquake", "jma-xml"],
            earthquakeEvent.Reports.Select(report => report.Source.SourceId));
    }

    [Fact]
    public void Merge_P2pSequenceAndJmaXmlMatchingEvent_CreatesOneEvent()
    {
        EarthquakeReport scalePrompt = CreateReport(
            "P2P-551",
            "p2p-scale-prompt",
            1,
            eventId: "p2pquake:p2p-scale-prompt",
            sourceId: "p2pquake") with
        {
            ReportType = EarthquakeReportType.SeismicIntensity,
            Hypocenter = null,
            Magnitude = null,
        };
        EarthquakeReport destination = CreateReport(
            "P2P-551",
            "p2p-destination",
            2,
            eventId: "p2pquake:p2p-destination",
            sourceId: "p2pquake") with
        {
            ReportType = EarthquakeReportType.Hypocenter,
        };
        EarthquakeReport detailScale = CreateReport(
            "P2P-551",
            "p2p-detail-scale",
            3,
            eventId: "p2pquake:p2p-detail-scale",
            sourceId: "p2pquake");
        EarthquakeReport jma = CreateReport(
            "VXSE53",
            "jma-detail",
            3,
            eventId: "20260825180733",
            sourceId: "jma-xml");

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([scalePrompt, destination, detailScale, jma]));

        Assert.Equal("20260825180733", earthquakeEvent.EventId);
        Assert.Equal(4, earthquakeEvent.Reports.Length);
        Assert.Equal(3, earthquakeEvent.Reports.Count(report =>
            report.Source.SourceId == "p2pquake"));
        Assert.Equal("jma-xml", earthquakeEvent.PreferredReport?.Source.SourceId);
    }

    [Fact]
    public void Merge_CoordinateLessP2pReports_DoesNotGuessAssociation()
    {
        EarthquakeReport first = CreateReport(
            "P2P-551",
            "p2p-scale-prompt-a",
            1,
            eventId: "p2pquake:p2p-scale-prompt-a",
            sourceId: "p2pquake") with
        {
            ReportType = EarthquakeReportType.SeismicIntensity,
            Hypocenter = null,
            Magnitude = null,
        };
        EarthquakeReport second = CreateReport(
            "P2P-551",
            "p2p-scale-prompt-b",
            2,
            eventId: "p2pquake:p2p-scale-prompt-b",
            sourceId: "p2pquake") with
        {
            ReportType = EarthquakeReportType.SeismicIntensity,
            Hypocenter = null,
            Magnitude = null,
        };

        Assert.Equal(2, EarthquakeEventMerger.Merge([first, second]).Length);
    }

    [Fact]
    public void Merge_SameP2pReportStage_DoesNotMergeNearbyEvents()
    {
        EarthquakeReport first = CreateReport(
            "P2P-551",
            "p2p-detail-a",
            1,
            eventId: "p2pquake:p2p-detail-a",
            sourceId: "p2pquake");
        EarthquakeReport second = CreateReport(
            "P2P-551",
            "p2p-detail-b",
            2,
            eventId: "p2pquake:p2p-detail-b",
            sourceId: "p2pquake");

        Assert.Equal(2, EarthquakeEventMerger.Merge([first, second]).Length);
    }

    [Fact]
    public void Merge_DistantP2pAndJmaXmlMatchingEvent_CreatesOneEvent()
    {
        GeoCoordinate coordinate = new(-15.4, 167.8);
        EarthquakeReport jma = CreateReport(
            "VXSE53",
            "jma-foreign",
            2,
            eventId: "20260825184000",
            sourceId: "jma-xml",
            magnitude: null,
            intensity: JmaIntensity.Unknown) with
        {
            ReportType = EarthquakeReportType.DistantEarthquake,
            DistantEarthquakeKind = DistantEarthquakeKind.VolcanicEruption,
            Hypocenter = new Hypocenter("南太平洋", "950", coordinate, null),
        };
        EarthquakeReport p2p = CreateReport(
            "P2P-551",
            "p2p-foreign",
            1,
            eventId: "p2pquake:p2p-foreign",
            sourceId: "p2pquake",
            magnitude: null,
            intensity: JmaIntensity.Unknown) with
        {
            ReportType = EarthquakeReportType.DistantEarthquake,
            DistantEarthquakeKind = DistantEarthquakeKind.VolcanicEruption,
            Hypocenter = new Hypocenter("バヌアツ諸島", null, coordinate, null),
        };

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([p2p, jma]));

        Assert.Equal("20260825184000", earthquakeEvent.EventId);
        Assert.Equal("jma-xml", earthquakeEvent.PreferredReport?.Source.SourceId);
    }

    [Fact]
    public void Merge_P2pAndJmaXmlDifferentOrigin_DoesNotMerge()
    {
        EarthquakeReport jma = CreateReport(
            "VXSE53",
            "jma-message",
            2,
            eventId: "jma-event",
            sourceId: "jma-xml");
        EarthquakeReport p2p = CreateReport(
            "P2P-551",
            "p2p-message",
            1,
            eventId: "p2pquake:p2p-message",
            sourceId: "p2pquake") with
        {
            OriginTime = BaseTime.AddMinutes(2),
        };

        Assert.Equal(2, EarthquakeEventMerger.Merge([p2p, jma]).Length);
    }

    [Fact]
    public void Merge_JmaTemporaryEventIds_Vxse51AndVxse53CreateOneEvent()
    {
        EarthquakeReport intensity = CreateReport(
            "VXSE51",
            "temporary-51",
            1,
            eventId: "20260824040519");
        EarthquakeReport detail = CreateReport(
            "VXSE53",
            "temporary-53",
            2,
            eventId: "20260824040526");

        EarthquakeEvent earthquakeEvent = Assert.Single(
            EarthquakeEventMerger.Merge([intensity, detail]));

        Assert.Equal("20260824040526", earthquakeEvent.EventId);
    }

    [Fact]
    public void Merge_JmaTemporaryEventIds_DifferentIntensityDoesNotMerge()
    {
        EarthquakeReport intensity = CreateReport(
            "VXSE51",
            "temporary-51",
            1,
            eventId: "20260824040519",
            intensity: JmaIntensity.Three);
        EarthquakeReport hypocenter = CreateReport(
            "VXSE52",
            "temporary-52",
            2,
            eventId: "20260824040526",
            intensity: JmaIntensity.Four);

        Assert.Equal(2, EarthquakeEventMerger.Merge([intensity, hypocenter]).Length);
    }

    [Fact]
    public void Merge_JmaTemporaryEventIds_OutsideTimeWindowDoesNotMerge()
    {
        EarthquakeReport intensity = CreateReport(
            "VXSE51",
            "temporary-51",
            1,
            eventId: "20260824040519");
        EarthquakeReport hypocenter = CreateReport(
            "VXSE52",
            "temporary-52",
            10,
            eventId: "20260824040630");

        Assert.Equal(2, EarthquakeEventMerger.Merge([intensity, hypocenter]).Length);
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
