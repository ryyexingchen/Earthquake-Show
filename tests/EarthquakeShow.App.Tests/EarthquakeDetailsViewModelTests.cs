using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class EarthquakeDetailsViewModelTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task Details_SelectingEarlierReport_UpdatesSummaryTimelineAndMapSnapshot()
    {
        EarthquakeReport first = CreateReport(
            "first",
            BaseTime,
            magnitude: 3.8,
            intensity: JmaIntensity.Three,
            station: true);
        EarthquakeReport correction = CreateReport(
            "correction",
            BaseTime.AddMinutes(2),
            status: ReportStatus.Correction,
            magnitude: 3.9,
            intensity: JmaIntensity.Four,
            station: false);
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([first, correction]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Equal("correction", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal(2, details.TimelineItems.Count);
        Assert.Contains("震级 M 3.8 → M 3.9", details.TimelineItems[1].ChangeSummary);
        Assert.Equal("M 3.9 (Mj)", GetField(details, "震级"));
        Assert.DoesNotContain(map.Markers, marker => marker.Kind == EarthquakeMapMarkerKind.Station);

        details.GoPreviousReport();

        Assert.Equal("first", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal("M 3.8 (Mj)", GetField(details, "震级"));
        Assert.Contains(map.Markers, marker => marker.Kind == EarthquakeMapMarkerKind.Station);
        Assert.True(details.CanGoNext);
        Assert.False(details.CanGoPrevious);

        details.GoNextReport();
        Assert.Equal("correction", page.State.ViewedReport?.Source.SourceMessageId);
        details.ReturnToLatestReport();
        Assert.Equal("correction", page.State.ViewedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task Details_ObservationSearchAndSelection_FocusesKnownCoordinate()
    {
        EarthquakeReport report = CreateReport(
            "observations",
            BaseTime,
            intensity: JmaIntensity.Four,
            station: true);
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Contains(details.Observations, item => item.Kind == "区域");
        Assert.Contains(details.Observations, item => item.LocationText == "位置未知");
        EarthquakeObservationItemViewModel station = Assert.Single(
            details.Observations,
            item => item.Kind == "观测点");
        details.SelectedObservation = station;

        Assert.Equal(station.Coordinate, map.FocusedCoordinate);
        details.ObservationSearchText = "不存在";
        Assert.Empty(details.Observations);
        details.ObservationSearchText = "";
        details.ShowHighestOnly = true;
        Assert.All(details.Observations, item => Assert.Equal(JmaIntensity.Four, item.Intensity));
    }

    [Fact]
    public async Task Details_RawPayloadIsPreservedAndTimelineNavigationStopsAtBounds()
    {
        const string raw = "<Report>原始内容</Report>";
        EarthquakeReport report = CreateReport("raw", BaseTime, rawPayload: raw);
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Equal(raw, details.RawPayload);
        Assert.False(details.CanGoPrevious);
        Assert.False(details.CanGoNext);
        Assert.False(details.CanReturnToLatest);
        details.GoPreviousReport();
        details.GoNextReport();
        Assert.Equal("raw", page.State.ViewedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task Details_ShowsDifferencesBetweenSourcesInSameEvent()
    {
        EarthquakeReport json = CreateReport(
            "json-summary",
            BaseTime,
            magnitude: 3.7,
            intensity: JmaIntensity.Two,
            station: false,
            sourceId: "jma-json");
        EarthquakeReport xml = CreateReport(
            "xml-detail",
            BaseTime,
            magnitude: 3.8,
            intensity: JmaIntensity.Three,
            station: true,
            sourceId: "jma-xml");
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([json, xml]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        EarthquakeSourceDifferenceItemViewModel difference = Assert.Single(details.SourceDifferences);
        Assert.Equal("jma-json", difference.SourceId);
        Assert.Equal("JMA JSON 摘要", difference.PriorityText);
        Assert.Contains("震度 3 → 2", difference.DifferenceText);
        Assert.Contains("震级 M 3.8 → M 3.7", difference.DifferenceText);
        Assert.Contains("观测点 1 → 0", difference.DifferenceText);
    }

    [Fact]
    public async Task Details_ShowsCandidateAssociationWithoutMergingEvents()
    {
        EarthquakeReport jma = CreateReport(
            "jma-report",
            BaseTime,
            magnitude: 3.8,
            intensity: JmaIntensity.Three,
            station: false,
            sourceId: "jma-xml",
            eventId: "jma-event");
        EarthquakeReport p2p = CreateReport(
            "p2p-report",
            BaseTime.AddSeconds(5),
            magnitude: 3.9,
            intensity: JmaIntensity.Three,
            station: false,
            sourceId: "p2pquake",
            eventId: "p2pquake:p2p-report");
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([jma, p2p]));
        await page.LoadAsync();
        Assert.True(page.SelectEvent("jma-event"));
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        EarthquakeEventAssociationItemViewModel association =
            Assert.Single(details.EventAssociations);
        Assert.Equal("p2pquake:p2p-report", association.EventId);
        Assert.Equal("p2pquake", association.SourceId);
        Assert.Equal("高置信度", association.ConfidenceText);
        Assert.Contains("时间差 0 秒", association.MatchText);
        Assert.Equal(2, page.State.Events.Length);
    }

    private static string GetField(EarthquakeDetailsViewModel details, string label)
    {
        return details.SummaryFields.Single(field => field.Label == label).Value;
    }

    private static EarthquakeReport CreateReport(
        string sourceMessageId,
        DateTimeOffset issuedAt,
        ReportStatus status = ReportStatus.Issued,
        double magnitude = 3.8,
        JmaIntensity intensity = JmaIntensity.Three,
        bool station = false,
        string? rawPayload = null,
        string sourceId = "jma-xml",
        string eventId = "details-event")
    {
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = "VXSE53",
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            Status = status,
            Context = ReportContext.Normal,
            Serial = status == ReportStatus.Correction ? 2 : 1,
            OriginTime = BaseTime,
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Hypocenter = new Hypocenter(
                "熊本県熊本",
                "741",
                new GeoCoordinate(32.8, 130.7),
                20),
            Magnitude = new Magnitude(magnitude, "Mj"),
            MaxIntensity = intensity,
            IntensityAreas =
            [
                new IntensityArea("741", "熊本県熊本", "43", "熊本県", intensity),
            ],
            IntensityStations = station
                ? [new IntensityStation(
                    "KMM001",
                    "熊本観測点",
                    "741",
                    intensity,
                    new GeoCoordinate(32.81, 130.71))]
                : [],
            Source = new SourceReference(
                sourceId,
                sourceMessageId,
                SourcePayload: rawPayload),
        };
    }

    private const string GeometryJson = """
        {
          "type": "FeatureCollection",
          "metadata": { "source": "测试离线轮廓", "officialBoundary": false },
          "features": [
            {
              "type": "Feature",
              "properties": { "areaCode": "741", "name": "熊本県熊本", "officialBoundary": false },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[130.4,32.4],[131.0,32.4],[131.0,33.1],[130.4,33.1],[130.4,32.4]]]
              }
            }
          ]
        }
        """;
}
