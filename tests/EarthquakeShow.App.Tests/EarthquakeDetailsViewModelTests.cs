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
        Assert.Contains(map.Markers, marker => marker.Kind == EarthquakeMapMarkerKind.Station);

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
        EarthquakeObservationItemViewModel area = Assert.Single(
            details.Observations,
            item => item.Kind == "区域");
        Assert.Equal("单击定位", area.LocationText);
        details.SelectedObservation = area;
        Assert.Equal(area.Coordinate, map.FocusedCoordinate);

        Assert.Contains(details.Observations, item =>
            item.Kind == "区域" && item.LocationText == "单击定位");
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
    public async Task Details_BuildsObservationTreeAndKeepsUnmappedStation()
    {
        EarthquakeReport report = CreateReport(
            "tree",
            BaseTime,
            intensity: JmaIntensity.Four) with
        {
            IntensityMunicipalities =
            [
                new IntensityMunicipality("C1", "熊本市", "741", JmaIntensity.Four),
            ],
            IntensityStations =
            [
                new IntensityStation(
                    "KMM001",
                    "熊本观测点",
                    "C1",
                    JmaIntensity.Four,
                    new GeoCoordinate(32.81, 130.71)),
                new IntensityStation(
                    "KMM002",
                    "未映射观测点",
                    "MISSING",
                    JmaIntensity.Two,
                    null),
            ],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        EarthquakeObservationTreeNode prefecture = Assert.Single(
            details.ObservationTreeNodes,
            node => node.Kind == "都道府县");
        EarthquakeObservationTreeNode area = Assert.Single(
            prefecture.Children,
            node => node.Kind == "区域");
        EarthquakeObservationTreeNode municipality = Assert.Single(
            area.Children,
            node => node.Kind == "市町村");
        EarthquakeObservationTreeNode station = Assert.Single(
            municipality.Children,
            node => node.Kind == "观测点");
        Assert.Equal("C1", municipality.Code);
        Assert.Equal("KMM001", station.Code);

        EarthquakeObservationTreeNode unmapped = Assert.Single(
            details.ObservationTreeNodes,
            node => node.Kind == "未映射");
        Assert.Contains(unmapped.Children, node => node.Code == "KMM002");

        details.SelectObservationNode(station);
        Assert.Equal(station.Observation?.Coordinate, map.FocusedCoordinate);

        details.ObservationSearchText = "熊本市";
        Assert.Single(details.ObservationTreeNodes);
        Assert.Equal("熊本県熊本", details.ObservationTreeNodes[0].Children[0].Name);
        details.ObservationSearchText = string.Empty;
        details.ShowHighestOnly = true;
        Assert.Contains(details.ObservationTreeNodes, node => node.Kind == "都道府县");
        Assert.DoesNotContain(details.ObservationTreeNodes, node => node.Kind == "未映射");
    }

    [Fact]
    public async Task Details_MunicipalityNode_FocusesMatchedGeometry()
    {
        EarthquakeReport report = CreateReport(
            "municipality-focus",
            BaseTime,
            intensity: JmaIntensity.Four) with
        {
            IntensityMunicipalities =
            [new IntensityMunicipality("C1", "熊本市", "741", JmaIntensity.Four)],
            IntensityStations = [],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        EarthquakeObservationTreeNode municipality = Assert.Single(
            Assert.Single(details.ObservationTreeNodes).Children);
        Assert.Equal("单击定位", municipality.LocationText);
        details.SelectObservationNode(municipality);
        Assert.Equal(municipality.Coordinate, map.FocusedCoordinate);
    }

    [Fact]
    public async Task Details_SelectionHighlightsPrefectureAreaMunicipalityAndStation()
    {
        EarthquakeReport report = CreateReport(
            "selection-highlight",
            BaseTime,
            station: true) with
        {
            IntensityMunicipalities =
            [new IntensityMunicipality("C1", "熊本市", "741", JmaIntensity.Four)],
            IntensityStations =
            [new IntensityStation(
                "KMM001",
                "熊本観測点",
                "C1",
                JmaIntensity.Four,
                new GeoCoordinate(32.81, 130.71))],
        };
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson),
            OfflineMapGeometry.LoadFromJson(MunicipalityGeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        EarthquakeObservationTreeNode prefecture = Assert.Single(
            details.ObservationTreeNodes,
            node => node.Kind == "都道府县");
        details.SelectObservationNode(prefecture);
        Assert.Equal(EarthquakeMapSelectionKind.Prefecture, map.SelectedMapSelection?.Kind);
        Assert.Equal("43", map.SelectedMapSelection?.Code);
        Assert.Single(map.SelectedAreaHighlights);

        EarthquakeObservationTreeNode area = Assert.Single(
            prefecture.Children,
            node => node.Kind == "区域");
        details.SelectObservationNode(area);
        Assert.Equal(EarthquakeMapSelectionKind.Area, map.SelectedMapSelection?.Kind);
        Assert.Single(map.SelectedAreaHighlights);
        Assert.True(map.TryGetSelectedObservationView(
            out GeoCoordinate areaCenter,
            out MapGeometryBounds areaBounds));
        Assert.Equal(32.75, areaCenter.Latitude, precision: 2);
        Assert.Equal(130.70, areaCenter.Longitude, precision: 2);
        Assert.Equal(130.4, areaBounds.MinLongitude, precision: 3);
        Assert.Equal(131.0, areaBounds.MaxLongitude, precision: 3);

        EarthquakeObservationTreeNode municipality = Assert.Single(
            area.Children,
            node => node.Kind == "市町村");
        details.SelectObservationNode(municipality);
        Assert.Equal(EarthquakeMapSelectionKind.Municipality, map.SelectedMapSelection?.Kind);
        Assert.Single(map.SelectedMunicipalityHighlights);

        EarthquakeObservationTreeNode station = Assert.Single(
            municipality.Children,
            node => node.Kind == "观测点");
        details.SelectObservationNode(station);
        Assert.Equal(EarthquakeMapSelectionKind.Station, map.SelectedMapSelection?.Kind);
        Assert.Equal("KMM001", map.SelectedStationHighlight?.Code);
        Assert.Equal(station.Coordinate, map.SelectedMapSelection?.Coordinate);

        details.ToggleObservationNode(station);
        Assert.Null(map.SelectedMapSelection);
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
            sourceId: "p2pquake");
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
        Assert.Equal("p2pquake", difference.SourceId);
        Assert.Equal("第三方补充", difference.PriorityText);
        Assert.Contains("震度 3 → 2", difference.DifferenceText);
        Assert.Contains("震级 M 3.8 → M 3.7", difference.DifferenceText);
        Assert.Contains("观测点 1 → 0", difference.DifferenceText);
    }

    [Fact]
    public async Task Details_MergesP2pAndJmaXmlAndAllowsSourceToggle()
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
        Assert.True(
            page.SelectEvent("jma-event"),
            string.Join(",", page.State.Events.Select(item =>
                $"{item.EventId}:{string.Join('|', item.Reports.Select(report => report.ReportCode))}")));
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Single(page.State.Events);
        Assert.False(details.HasEventAssociations);
        Assert.True(details.CanToggleSource);
        Assert.Equal("jma-xml", page.State.ViewedReport?.Source.SourceId);
        EarthquakeTimelineItemViewModel jmaTimelineItem =
            Assert.Single(details.TimelineItems);
        Assert.Equal("jma-xml", jmaTimelineItem.SourceId);

        details.ToggleSource();

        Assert.Equal("p2pquake", page.State.ViewedReport?.Source.SourceId);
        Assert.Equal("p2p-report", page.State.ViewedReport?.Source.SourceMessageId);
        EarthquakeTimelineItemViewModel p2pTimelineItem =
            Assert.Single(details.TimelineItems);
        Assert.Equal("p2pquake", p2pTimelineItem.SourceId);
    }

    [Fact]
    public async Task Details_TimelineNavigationStaysWithinSelectedSource()
    {
        EarthquakeReport jmaFirst = CreateReport(
            "jma-first",
            BaseTime,
            sourceId: "jma-xml",
            eventId: "multi-source-event");
        EarthquakeReport p2pFirst = CreateReport(
            "p2p-first",
            BaseTime.AddMinutes(1),
            sourceId: "p2pquake",
            eventId: "multi-source-event");
        EarthquakeReport jmaLatest = CreateReport(
            "jma-latest",
            BaseTime.AddMinutes(2),
            sourceId: "jma-xml",
            eventId: "multi-source-event");
        EarthquakeReport p2pLatest = CreateReport(
            "p2p-latest",
            BaseTime.AddMinutes(3),
            sourceId: "p2pquake",
            eventId: "multi-source-event");
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository(
                [jmaFirst, p2pFirst, jmaLatest, p2pLatest]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Equal("jma-latest", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.StartsWith("第 2 / 2 报", details.SnapshotText, StringComparison.Ordinal);
        Assert.Equal(2, details.TimelineItems.Count);
        details.GoPreviousReport();
        Assert.Equal("jma-first", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.StartsWith("第 1 / 2 报", details.SnapshotText, StringComparison.Ordinal);
        details.ReturnToLatestReport();
        Assert.Equal("jma-latest", page.State.ViewedReport?.Source.SourceMessageId);

        details.ToggleSource();
        Assert.Equal("p2p-latest", page.State.ViewedReport?.Source.SourceMessageId);
        Assert.StartsWith("第 2 / 2 报", details.SnapshotText, StringComparison.Ordinal);
        Assert.Equal(2, details.TimelineItems.Count);
        details.GoPreviousReport();
        Assert.Equal("p2p-first", page.State.ViewedReport?.Source.SourceMessageId);
        details.ReturnToLatestReport();
        Assert.Equal("p2p-latest", page.State.ViewedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task Details_PersistsReceivedFieldsAndSkipsUnknownChanges()
    {
        EarthquakeReport intensity = CreateReport(
            "intensity",
            BaseTime,
            reportType: EarthquakeReportType.SeismicIntensity,
            magnitude: null,
            includeHypocenter: false,
            intensity: JmaIntensity.Three);
        EarthquakeReport hypocenter = CreateReport(
            "hypocenter",
            BaseTime.AddMinutes(1),
            reportType: EarthquakeReportType.Hypocenter,
            magnitude: 3.8,
            intensity: JmaIntensity.Unknown,
            tsunamiComment: "この地震による津波の心配はありません。");
        EarthquakeReport update = CreateReport(
            "intensity-update",
            BaseTime.AddMinutes(2),
            reportType: EarthquakeReportType.SeismicIntensity,
            magnitude: null,
            includeHypocenter: false,
            intensity: JmaIntensity.Four);
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([intensity, hypocenter, update]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Contains("最大震度：3", details.TimelineItems[0].ChangeSummary);
        Assert.Contains("震源・规模：调查中", details.TimelineItems[0].ChangeSummary);
        Assert.Contains("海啸：津波 调查中", details.TimelineItems[0].ChangeSummary);
        Assert.DoesNotContain("3 → 不明", details.TimelineItems[1].ChangeSummary);
        Assert.Contains("海啸：津波の心配なし", details.TimelineItems[1].ChangeSummary);
        Assert.Contains("最大震度 3 → 4", details.TimelineItems[2].ChangeSummary);
        Assert.Contains("震级：M 3.8", details.TimelineItems[2].ChangeSummary);
        Assert.Contains("海啸：津波の心配なし", details.TimelineItems[2].ChangeSummary);
        Assert.Equal("Four", details.SummaryOverview.MaximumIntensity?.Kind);
        Assert.Equal("Three", details.TimelineItems[0].Summary.MaximumIntensity?.Kind);
        Assert.True(details.TimelineItems[0].Summary.HasSourceScaleInvestigation);
        Assert.Equal("津波 调查中", details.TimelineItems[0].Summary.TsunamiStatus.Text);
        Assert.Equal("津波の心配なし", details.TimelineItems[2].Summary.TsunamiStatus.Text);
        Assert.Equal("M 3.8 (Mj)", GetField(details, "震级"));
        Assert.Equal("津波の心配なし", details.TsunamiStatus.Text);
        Assert.Equal("NoConcern", details.TsunamiStatus.Kind);
    }

    [Fact]
    public async Task Details_GenericTsunamiTemplate_DoesNotInferMajorWarning()
    {
        EarthquakeReport report = CreateReport(
            "generic-tsunami",
            BaseTime,
            tsunamiComment: "津波警報等（大津波警報・津波警報あるいは津波注意報）を発表中です。");
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Equal("津波 调查中", details.TsunamiStatus.Text);
        Assert.Equal("Investigating", details.TsunamiStatus.Kind);
    }

    [Fact]
    public async Task Details_UnknownTsunamiText_RemainsInvestigating()
    {
        EarthquakeReport report = CreateReport(
            "unknown-tsunami",
            BaseTime,
            tsunamiComment: "津波に関する新しい説明文です。");
        using var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([report]));
        await page.LoadAsync();
        using var map = new EarthquakeMapViewModel(
            page,
            OfflineMapGeometry.LoadFromJson(GeometryJson));
        using var details = new EarthquakeDetailsViewModel(page, map);

        Assert.Equal("津波に関する新しい説明文です。", details.TsunamiStatus.Text);
        Assert.Equal("Investigating", details.TsunamiStatus.Kind);
    }

    private static string GetField(EarthquakeDetailsViewModel details, string label)
    {
        return details.SummaryFields.Single(field => field.Label == label).Value;
    }

    private static EarthquakeReport CreateReport(
        string sourceMessageId,
        DateTimeOffset issuedAt,
        ReportStatus status = ReportStatus.Issued,
        double? magnitude = 3.8,
        JmaIntensity intensity = JmaIntensity.Three,
        bool station = false,
        string? rawPayload = null,
        string sourceId = "jma-xml",
        string eventId = "details-event",
        EarthquakeReportType reportType = EarthquakeReportType.HypocenterAndIntensity,
        bool includeHypocenter = true,
        string? tsunamiComment = null)
    {
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = "VXSE53",
            ReportType = reportType,
            Status = status,
            Context = ReportContext.Normal,
            Serial = status == ReportStatus.Correction ? 2 : 1,
            OriginTime = BaseTime,
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Hypocenter = includeHypocenter
                ? new Hypocenter(
                    "熊本県熊本",
                    "741",
                    new GeoCoordinate(32.8, 130.7),
                    20)
                : null,
            Magnitude = magnitude is double value ? new Magnitude(value, "Mj") : null,
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
            TsunamiComment = tsunamiComment,
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

    private const string MunicipalityGeometryJson = """
        {
          "type": "FeatureCollection",
          "metadata": { "source": "测试市町村轮廓", "officialBoundary": true },
          "features": [
            {
              "type": "Feature",
              "properties": { "municipalityCode": "C1", "name": "熊本市" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[130.5,32.5],[130.9,32.5],[130.9,32.9],[130.5,32.9],[130.5,32.5]]]
              }
            }
          ]
        }
        """;
}
