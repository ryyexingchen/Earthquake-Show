using System.Collections.Immutable;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class TsunamiPageViewModelTests
{
    [Fact]
    public async Task Load_SortsReportsAndPreservesSelection()
    {
        DateTimeOffset oldIssuedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        DateTimeOffset newIssuedAt = oldIssuedAt.AddHours(1);
        JmaTsunamiReport older = CreateReport("old", oldIssuedAt);
        JmaTsunamiReport newer = CreateReport("new", newIssuedAt);
        var repository = new StubTsunamiReportRepository([older, newer]);
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Equal(TsunamiPageLoadState.Ready, viewModel.State.LoadState);
        Assert.Equal(
            [newer.Source.SourceMessageId, older.Source.SourceMessageId],
            viewModel.State.Reports.Select(report => report.Source.SourceMessageId));
        Assert.Equal(newer.Source.SourceMessageId, viewModel.State.SelectedReport?.Source.SourceMessageId);
        Assert.False(viewModel.State.IsOffline);

        Assert.True(viewModel.SelectReport(
            older.EventId,
            older.Source.SourceId,
            older.Source.SourceMessageId));
        repository.Reports = [newer, older];
        await viewModel.LoadAsync();

        Assert.Equal(older.Source.SourceMessageId, viewModel.State.SelectedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task EarthquakeMagnitudeText_UsesDescriptionWhenNumericMagnitudeIsUnknown()
    {
        JmaTsunamiReport report = CreateReport(
            "magnitude-description",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9))) with
        {
            Magnitude = new Magnitude(null, "Mj", "不明", "Ｍ８を超える巨大地震"),
        };
        using var viewModel = new TsunamiPageViewModel(new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        Assert.Equal("Ｍ８を超える巨大地震", viewModel.EarthquakeMagnitudeText);
    }

    [Fact]
    public async Task EventReports_GroupsSameEventByLatestReportAndKeepsFullTimeline()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport first = CreateReport("event-first", issuedAt);
        JmaTsunamiReport correction = CreateReport("event-correction", issuedAt.AddMinutes(5)) with
        {
            Status = ReportStatus.Correction,
        };
        JmaTsunamiReport otherEvent = CreateReport("other-event", issuedAt.AddMinutes(1)) with
        {
            EventId = "event-2",
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([first, correction, otherEvent]));

        await viewModel.LoadAsync();

        Assert.Equal(2, viewModel.EventReports.Length);
        Assert.Equal("event-correction", viewModel.EventReports[0].Source.SourceMessageId);
        Assert.Equal("event-correction", viewModel.SelectedReport?.Source.SourceMessageId);
        Assert.True(viewModel.SelectReport(
            first.EventId,
            first.Source.SourceId,
            first.Source.SourceMessageId));
        Assert.Equal("event-first", viewModel.SelectedReport?.Source.SourceMessageId);
        Assert.Equal("event-correction", viewModel.SelectedEventReport?.Source.SourceMessageId);
        Assert.Equal(2, viewModel.TimelineReports.Length);
    }

    [Fact]
    public async Task EventReportDisplays_IncludeFormattedHypocenterDetails()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("event-display", issuedAt) with
        {
            Hypocenter = new Hypocenter("不明", null, null, 42, "遠地地震のため震源不明"),
            Magnitude = new Magnitude(null, "Mj", "不明", "Ｍ８を超える巨大地震"),
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        TsunamiEventReportDisplay display = Assert.Single(viewModel.EventReportDisplays);
        Assert.Equal(display.Identity, viewModel.SelectedEventReportIdentity);
        Assert.Equal("遠地地震のため震源不明", display.EarthquakeSourceText);
        Assert.Equal("Ｍ８を超える巨大地震", display.EarthquakeMagnitudeText);
        Assert.Equal("42 km", display.EarthquakeDepthText);
        Assert.Equal("2026-08-24 10:00:00 JST", display.IssuedAtText);
    }

    [Fact]
    public async Task SelectedReport_InheritsUnchangedAreasAndUpdatesRepeatedCodes()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport first = CreateReport("increment-1", issuedAt) with
        {
            Items =
            [
                new JmaTsunamiInformationItem("津波注意報", "34", null, null, [new JmaTsunamiArea("A地区", "A")]),
            ],
            ForecastAreas =
            [
                new JmaTsunamiForecastArea("A地区", "A", "津波注意報", "34", null, null, null, null, null, []),
            ],
        };
        JmaTsunamiReport second = CreateReport("increment-2", issuedAt.AddMinutes(5)) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation("B地区", "B", "B点", "B", null, null, null, "観測", null, null, new JmaTsunamiHeight(0.4, null, null, "m", null)),
            ],
        };
        JmaTsunamiReport third = CreateReport("increment-3", issuedAt.AddMinutes(10)) with
        {
            ForecastAreas =
            [
                new JmaTsunamiForecastArea("A地区", "A", "津波警報", "35", null, null, null, null, null, []),
                new JmaTsunamiForecastArea("C地区", "C", "津波注意報", "34", null, null, null, null, null, []),
            ],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([first, second, third]));

        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectReport(second.EventId, second.Source.SourceId, second.Source.SourceMessageId));
        Assert.Equal(["A"], viewModel.ForecastAreas.Select(area => area.Code));
        Assert.Equal(["B"], viewModel.ObservationStations.Select(station => station.Code));
        Assert.Equal("津波注意報", viewModel.ForecastAreas[0].LevelText);
        Assert.Contains(viewModel.InformationItems, item => item.AreasText.Contains("A地区", StringComparison.Ordinal));

        Assert.True(viewModel.SelectReport(third.EventId, third.Source.SourceId, third.Source.SourceMessageId));
        Assert.Equal(["A", "C"], viewModel.ForecastAreas.Select(area => area.Code));
        Assert.Equal("津波警報", viewModel.ForecastAreas[0].LevelText);
        Assert.Equal(["B"], viewModel.ObservationStations.Select(station => station.Code));
        Assert.False(viewModel.CanGoNext);
        viewModel.GoPreviousReport();
        Assert.Equal("increment-2", viewModel.SelectedReport?.Source.SourceMessageId);
        viewModel.GoNextReport();
        Assert.Equal("increment-3", viewModel.SelectedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task ForecastArea_ExposesChildStationForecastsAndSelection()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("forecast-stations", issuedAt) with
        {
            ForecastAreas =
            [
                new JmaTsunamiForecastArea(
                    "茨城県",
                    "300",
                    "大津波警報：発表",
                    "53",
                    "津波なし",
                    "00",
                    issuedAt.AddHours(1),
                    null,
                    new JmaTsunamiHeight(null, "巨大", "不明", "m", "津波の高さ"),
                    [
                        new JmaTsunamiStationForecast(
                            "大洗",
                            "30001",
                            issuedAt.AddHours(8),
                            issuedAt.AddHours(1),
                            null),
                    ]),
            ],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        TsunamiForecastAreaDisplay area = Assert.Single(viewModel.ForecastAreas);
        TsunamiStationForecastDisplay station = Assert.Single(area.Stations);
        Assert.Equal("30001", station.Code);
        Assert.Equal("2026-08-24 18:00:00 JST", station.HighTideText);
        Assert.True(viewModel.SelectForecastArea("300"));
        Assert.Equal("300", viewModel.SelectedForecastAreaCode);
        Assert.Equal("300", viewModel.SelectedForecastArea?.Code);
        Assert.True(viewModel.ToggleForecastAreaSelection("300"));
        Assert.Null(viewModel.SelectedForecastAreaCode);
        Assert.Null(viewModel.SelectedForecastArea);
        Assert.True(viewModel.ToggleForecastAreaSelection("300"));
        Assert.Equal("300", viewModel.SelectedForecastAreaCode);
        Assert.False(viewModel.SelectForecastArea("missing"));

        viewModel.ClearSelectedForecastArea();
        Assert.Null(viewModel.SelectedForecastArea);
    }

    [Fact]
    public async Task Timeline_InvestigationReportInheritsPreviousHighestAlert()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport first = CreateReport("timeline-alert-1", issuedAt) with
        {
            ForecastAreas =
            [
                new JmaTsunamiForecastArea("A地区", "A", "津波注意報", "34", null, null, null, null, null, []),
            ],
        };
        JmaTsunamiReport second = CreateReport("timeline-alert-2", issuedAt.AddMinutes(5)) with
        {
            ForecastAreas =
            [
                new JmaTsunamiForecastArea("A地区", "A", "大津波警報", "36", null, null, null, null, null, []),
            ],
        };
        JmaTsunamiReport third = CreateReport("timeline-alert-3", issuedAt.AddMinutes(10)) with
        {
            Items =
            [
                new JmaTsunamiInformationItem("津波 調査中", null, null, null, []),
            ],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([first, second, third]));

        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectReport(
            third.EventId,
            third.Source.SourceId,
            third.Source.SourceMessageId));

        Assert.Equal("大津波警報", viewModel.TimelineReports[^1].LevelText);
    }

    [Fact]
    public async Task LoadFailure_SetsErrorState()
    {
        var repository = new StubTsunamiReportRepository([], new InvalidDataException("测试数据损坏"));
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Equal(TsunamiPageLoadState.Error, viewModel.State.LoadState);
        Assert.Equal("测试数据损坏", viewModel.State.ErrorMessage);
        Assert.False(viewModel.State.IsOffline);
    }

    [Fact]
    public async Task Refresh_UsesRepositoryAndReloadsReports()
    {
        JmaTsunamiReport report = CreateReport(
            "refresh",
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9)));
        var repository = new StubTsunamiReportRepository([])
        {
            ReportsAfterRefresh = [report],
        };
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.RefreshAsync();

        Assert.Equal(1, repository.RefreshCalls);
        Assert.Contains(report, viewModel.State.Reports);
        Assert.False(viewModel.State.IsRefreshing);
    }

    [Fact]
    public async Task Refresh_PreservesSelectedReportIdentity()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport selected = CreateReport("selected", issuedAt);
        JmaTsunamiReport other = CreateReport("other", issuedAt.AddMinutes(1));
        var repository = new StubTsunamiReportRepository([selected, other])
        {
            ReportsAfterRefresh = [other, selected],
        };
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectReport(
            selected.EventId,
            selected.Source.SourceId,
            selected.Source.SourceMessageId));

        await viewModel.RefreshAsync();

        Assert.Equal(selected.Source.SourceMessageId, viewModel.SelectedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task Refresh_FallsBackToLatestOnlyWhenSelectedReportIsMissing()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport selected = CreateReport("selected-missing", issuedAt);
        JmaTsunamiReport latest = CreateReport("latest", issuedAt.AddMinutes(1));
        var repository = new StubTsunamiReportRepository([selected, latest])
        {
            ReportsAfterRefresh = [latest],
        };
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectReport(
            selected.EventId,
            selected.Source.SourceId,
            selected.Source.SourceMessageId));

        await viewModel.RefreshAsync();

        Assert.Equal(latest.Source.SourceMessageId, viewModel.SelectedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task SelectedReport_ProjectsDetailsAndTsunamiLevel()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("details", issuedAt) with
        {
            Items =
            [
                new JmaTsunamiInformationItem(
                    "津波注意報",
                    null,
                    null,
                    null,
                    [new JmaTsunamiArea("茨城県", "JP08")]),
            ],
            ForecastAreas =
            [
                new JmaTsunamiForecastArea(
                    "茨城県",
                    "JP08",
                    "津波注意報",
                    null,
                    null,
                    null,
                    issuedAt.AddHours(1),
                    "到達予定",
                    new JmaTsunamiHeight(0.5, null, null, "m", "高さ"),
                    []),
            ],
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "茨城県",
                    "JP08",
                    "大洗",
                    "ST01",
                    null,
                    issuedAt.AddHours(2),
                    null,
                    "微弱",
                    null,
                    null,
                    null),
            ],
            EstimationAreas =
            [
                new JmaTsunamiEstimationArea(
                    "茨城県沖",
                    "OFF01",
                    null,
                    null,
                    new JmaTsunamiHeight(null, "巨大", null, null, null)),
            ],
            Source = new SourceReference(
                "jma-xml-tsunami",
                "details",
                SourcePayload: "<Report><Headline>sample</Headline></Report>"),
        };
        var repository = new StubTsunamiReportRepository([report]);
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Equal(TsunamiLevel.Advisory, viewModel.SelectedReportLevel);
        Assert.Equal("津波注意報", viewModel.SelectedReportLevelText);
        Assert.Single(viewModel.ForecastAreas);
        Assert.Equal("0.5 m", viewModel.ForecastAreas[0].HeightText);
        Assert.Equal("到達予定", viewModel.ForecastAreas[0].ArrivalText.Split('（')[1].TrimEnd('）'));
        Assert.Single(viewModel.ObservationStations);
        Assert.Equal("微弱", viewModel.ObservationStations[0].InitialText);
        Assert.Single(viewModel.EstimationAreas);
        Assert.Equal("巨大", viewModel.EstimationAreas[0].HeightText);
        Assert.Single(viewModel.InformationItems);
        Assert.Equal("茨城県（JP08）", viewModel.InformationItems[0].AreasText);
        Assert.True(viewModel.CanCopyRawXml);
        Assert.Contains("<Report>", viewModel.RawXmlText);
        viewModel.MarkRawXmlCopied();
        Assert.Equal("已复制原始 XML", viewModel.RawXmlCopyStatus);
    }

    [Fact]
    public async Task SelectedReport_UsesJapanTimeAndUnknownHypocenterDescription()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        JmaTsunamiReport report = CreateReport("display-format", issuedAt) with
        {
            OriginTime = issuedAt,
            Hypocenter = new Hypocenter("不明", null, null, null, "遠地地震のため震源不明"),
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        Assert.Equal("遠地地震のため震源不明", viewModel.EarthquakeSourceText);
        Assert.Equal("2026-08-24 09:00:00 JST", viewModel.EarthquakeOriginTimeText);
        Assert.Equal("2026-08-24 09:00:00 JST", viewModel.SelectedReportIssuedAtText);
    }

    [Fact]
    public async Task ForecastAdvisoryWithoutHeight_ShowsPlaceholder()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("advisory-no-height", issuedAt) with
        {
            ForecastAreas =
            [
                new JmaTsunamiForecastArea(
                    "北海道",
                    "191",
                    "津波注意報",
                    "34",
                    null,
                    null,
                    null,
                    null,
                    null,
                    []),
            ],
            FixedAdditionalTexts = ["沿岸では津波に注意してください。"],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        Assert.Equal("——", Assert.Single(viewModel.ForecastAreas).HeightText);
        Assert.Single(viewModel.FixedAdditionalTexts);
        Assert.Equal("沿岸では津波に注意してください。", viewModel.FixedAdditionalTexts[0]);
        Assert.True(viewModel.HasFixedAdditionalTexts);
    }

    [Fact]
    public async Task Load_UsesPersistedCatalogForExactStationCodeMapping()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("catalog", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "釧路沖",
                    "00410",
                    "报文名称",
                    "10050",
                    null,
                    null,
                    null,
                    null,
                    issuedAt,
                    null,
                    new JmaTsunamiHeight(0.4, null, null, "m", "高さ")),
            ],
        };
        var repository = new CatalogStubTsunamiReportRepository(
            [report],
            JmaTsunamiStationCatalog.Create(
                "sqlite-test",
                [new JmaTsunamiStationCatalogEntry("10050", "数据库站点", null, 42.1, 144.2, "100")],
                [new JmaTsunamiPublicationCatalogEntry("00410", "釧路沖１００ｋｍ", null, ["10050"])]));
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Equal("数据库站点", viewModel.ObservationStations[0].Name);
        Assert.Equal(42.1, viewModel.ObservationStations[0].Latitude);
        Assert.Equal("00410", viewModel.ObservationStations[0].PublicationCode);
        Assert.True(viewModel.ObservationStations[0].IsCatalogMatched);
    }

    [Fact]
    public async Task OffshoreStation_UsesOffshoreLabelWhenReportAreaIsMissing()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("offshore-area", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "",
                    "",
                    "近海站点",
                    "10050",
                    null,
                    null,
                    null,
                    "観測",
                    null,
                    null,
                    new JmaTsunamiHeight(0.4, null, null, "m", "高さ")),
                new JmaTsunamiObservationStation(
                    "",
                    "",
                    "沿岸站点",
                    "COASTAL",
                    null,
                    null,
                    null,
                    "観測",
                    null,
                    null,
                    new JmaTsunamiHeight(0.3, null, null, "m", "高さ")),
            ],
        };
        var repository = new CatalogStubTsunamiReportRepository(
            [report],
            JmaTsunamiStationCatalog.Create(
                "offshore-test",
                [
                    new JmaTsunamiStationCatalogEntry("10050", "近海站点", null, 42.1, 144.2, null),
                    new JmaTsunamiStationCatalogEntry("COASTAL", "沿岸站点", null, 35.0, 139.0, null),
                ],
                [new JmaTsunamiPublicationCatalogEntry("00410", "釧路沖１００ｋｍ", null, ["10050"])]));
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Equal("沖合", viewModel.ObservationStations.Single(item => item.Code == "10050").AreaName);
        Assert.Equal(string.Empty, viewModel.ObservationStations.Single(item => item.Code == "COASTAL").AreaName);
    }

    [Fact]
    public async Task Load_CatalogReadFailure_UsesFallbackCatalog()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("catalog-fallback", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "茨城県",
                    "JP08",
                    "报文名称",
                    "10050",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new JmaTsunamiHeight(0.4, null, null, "m", "高さ")),
            ],
        };
        var repository = new CatalogStubTsunamiReportRepository(
            [report],
            JmaTsunamiStationCatalog.Empty,
            new InvalidDataException("目录损坏"));
        JmaTsunamiStationCatalog fallback = JmaTsunamiStationCatalog.Create(
            "json-test",
            [new JmaTsunamiStationCatalogEntry("10050", "固定目录站点", null, 42.2, 144.3, "100")],
            []);
        using var viewModel = new TsunamiPageViewModel(repository, fallback);

        await viewModel.LoadAsync();

        Assert.Equal("固定目录站点", viewModel.ObservationStations[0].Name);
        Assert.Equal(42.2, viewModel.ObservationStations[0].Latitude);
    }

    [Fact]
    public async Task SelectObservationStation_ExposesDetailsAndClearsWhenReportChanges()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("station-selection", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "釧路沖",
                    "00410",
                    "报文名称",
                    "10050",
                    null,
                    issuedAt.AddMinutes(2),
                    "到達",
                    "観測",
                    issuedAt.AddMinutes(8),
                    null,
                    new JmaTsunamiHeight(0.4, null, null, "m", "高さ")),
            ],
        };
        var repository = new CatalogStubTsunamiReportRepository(
            [report],
            JmaTsunamiStationCatalog.Create(
                "selection-test",
                [new JmaTsunamiStationCatalogEntry("10050", "数据库站点", null, 42.1, 144.2, "100")],
                [new JmaTsunamiPublicationCatalogEntry("00410", "釧路沖１００ｋｍ", null, ["10050"])]));
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.True(viewModel.SelectObservationStation("10050"));
        Assert.True(viewModel.HasSelectedObservationStation);
        Assert.Equal("数据库站点", viewModel.SelectedObservationStation?.Name);
        Assert.Equal("00410", viewModel.SelectedObservationStation?.PublicationCode);
        Assert.Equal("观测到海啸", viewModel.SelectedObservationStation?.ObservationStatusText);
        Assert.True(viewModel.ToggleObservationStationSelection("10050"));
        Assert.False(viewModel.HasSelectedObservationStation);
        Assert.Null(viewModel.SelectedObservationStation);
        Assert.True(viewModel.ToggleObservationStationSelection("10050"));
        Assert.Equal("10050", viewModel.SelectedObservationStation?.Code);
        Assert.False(viewModel.SelectObservationStation("不存在"));
        viewModel.SelectedObservationStation = viewModel.ObservationStations[0];
        Assert.Equal("10050", viewModel.SelectedObservationStation?.Code);
        viewModel.SelectedObservationStation = null;
        Assert.False(viewModel.HasSelectedObservationStation);
        Assert.True(viewModel.SelectObservationStation("10050"));

        repository.Reports = [CreateReport("station-selection-next", issuedAt.AddMinutes(10))];
        await viewModel.LoadAsync();

        Assert.False(viewModel.HasSelectedObservationStation);
        Assert.Null(viewModel.SelectedObservationStation);
    }

    [Fact]
    public async Task SelectedObservationCoordinate_OnlyReturnsMeasuredStationWithFiniteCoordinates()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("station-coordinate", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "区域",
                    "有坐标",
                    "观测到海啸",
                    "MEASURED",
                    null,
                    null,
                    null,
                    "観測",
                    null,
                    null,
                    new JmaTsunamiHeight(0.4, null, null, "m", "高さ")),
                new JmaTsunamiObservationStation(
                    "区域",
                    "欠测",
                    "欠测",
                    "MISSING",
                    null,
                    null,
                    null,
                    "欠測",
                    null,
                    null,
                    new JmaTsunamiHeight(null, null, null, null, null)),
            ],
        };
        var repository = new CatalogStubTsunamiReportRepository(
            [report],
            JmaTsunamiStationCatalog.Create(
                "coordinate-test",
                [
                    new JmaTsunamiStationCatalogEntry("MEASURED", "有坐标", null, 42.1, 144.2, "100"),
                    new JmaTsunamiStationCatalogEntry("MISSING", "欠测", null, null, null, "100"),
                ],
                []));
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.True(viewModel.SelectObservationStation("MEASURED"));
        Assert.True(viewModel.TryGetSelectedObservationCoordinate(out GeoCoordinate coordinate));
        Assert.Equal(42.1, coordinate.Latitude);
        Assert.Equal(144.2, coordinate.Longitude);

        Assert.True(viewModel.SelectObservationStation("MISSING"));
        Assert.False(viewModel.TryGetSelectedObservationCoordinate(out _));
    }

    [Fact]
    public async Task ObservationStations_HidesNoTsunamiAndKeepsWeakObservation()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("station-filter", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation("区域", "A", "无海啸", "NO", null, null, null, null, null, null, new JmaTsunamiHeight(0, null, null, "m", "高さ")),
                new JmaTsunamiObservationStation("区域", "A", "微弱站", "WEAK", null, null, null, "微弱", null, null, new JmaTsunamiHeight(null, "微弱", null, null, null)),
            ],
        };
        var repository = new StubTsunamiReportRepository([report]);
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Single(viewModel.ObservationStations);
        Assert.Equal("WEAK", viewModel.ObservationStations[0].Code);
        Assert.True(viewModel.ObservationStations[0].HasMeasuredTsunami);
        Assert.Equal(string.Empty, viewModel.ObservationStations[0].StatusDisplayText);
        Assert.Equal("微弱", viewModel.ObservationStations[0].MeasuredHeightDisplayText);
    }

    [Fact]
    public async Task ObservationStation_StatusesMoveToMeasuredHeightDisplay()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("observation-status-display", issuedAt) with
        {
            ObservationStations =
            [
                new JmaTsunamiObservationStation("区域", "欠测", "欠测站", "MISSING", null, null, null, "欠測", null, null, new JmaTsunamiHeight(null, "欠測", null, null, null)),
                new JmaTsunamiObservationStation("区域", "观测中", "观测中站", "PENDING", null, null, null, "観測中", null, null, new JmaTsunamiHeight(null, "観測中", null, null, null)),
                new JmaTsunamiObservationStation("区域", "实测", "实测站", "MEASURED", null, null, null, "観測", null, null, new JmaTsunamiHeight(1.8, null, null, "m", "高さ")),
            ],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        Assert.Equal(
            ["欠测", "观测中", "1.8 m"],
            viewModel.ObservationStations.Select(item => item.MeasuredHeightDisplayText));
        Assert.Equal(
            [string.Empty, string.Empty, string.Empty],
            viewModel.ObservationStations.Select(item => item.StatusDisplayText));
    }

    [Fact]
    public async Task ObservationAndForecastSectionsRemainIndependent()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("observation-only", issuedAt) with
        {
            ForecastAreas = [],
            ObservationStations =
            [
                new JmaTsunamiObservationStation(
                    "北海道",
                    "JP01",
                    "釧路",
                    "ST01",
                    null,
                    issuedAt.AddMinutes(4),
                    "到達",
                    "観測",
                    issuedAt.AddMinutes(9),
                    null,
                    new JmaTsunamiHeight(0.3, null, null, "m", "高さ")),
            ],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasForecastAreas);
        Assert.True(viewModel.HasObservationStations);
        Assert.Contains("仅收到海啸观测", viewModel.ObservationSummaryText);
        Assert.Single(viewModel.ObservationStations);
    }

    [Fact]
    public async Task ObservationStation_UsesMeasuredHeightLevelForDisplayColor()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport report = CreateReport("observation-levels", issuedAt) with
        {
            ObservationStations =
            [
                CreateObservationStation("MINOR", 0.1),
                CreateObservationStation("ADVISORY", 0.5),
                CreateObservationStation("WARNING", 1.8),
                CreateObservationStation("MAJOR", 3.1),
            ],
        };
        using var viewModel = new TsunamiPageViewModel(
            new StubTsunamiReportRepository([report]));

        await viewModel.LoadAsync();

        Assert.Equal(
            [TsunamiLevel.MinorChange, TsunamiLevel.Advisory, TsunamiLevel.Warning, TsunamiLevel.MajorWarning],
            viewModel.ObservationStations.Select(item => item.Level));

        static JmaTsunamiObservationStation CreateObservationStation(string code, double meters) =>
            new(
                "区域",
                code,
                code,
                code,
                null,
                null,
                null,
                "観測",
                null,
                null,
                new JmaTsunamiHeight(meters, null, null, "m", "高さ"));
    }

    [Fact]
    public void MapZoom_UsesAvailableDetailLevelsAndOneStepIncrement()
    {
        using var viewModel = new TsunamiPageViewModel(new StubTsunamiReportRepository([]));

        Assert.Equal(TsunamiMapDetailLevel.Overview, viewModel.MapDetailLevel);
        Assert.Equal("低精度地图 · 1.0×", viewModel.MapStatusText);

        viewModel.ZoomMapIn();
        Assert.Equal(2, viewModel.MapZoomLevel);
        Assert.Equal(TsunamiMapDetailLevel.Overview, viewModel.MapDetailLevel);

        viewModel.ZoomMapIn();
        Assert.Equal(3, viewModel.MapZoomLevel);
        Assert.Equal(TsunamiMapDetailLevel.Medium, viewModel.MapDetailLevel);

        for (int index = 0; index < 9; index++)
        {
            viewModel.ZoomMapIn();
        }

        Assert.Equal(12, viewModel.MapZoomLevel);
        Assert.Equal(TsunamiMapDetailLevel.Medium, viewModel.MapDetailLevel);

        Assert.False(viewModel.ZoomMapIn());
        Assert.Equal(12, viewModel.MapZoomLevel);
        Assert.Equal(TsunamiMapDetailLevel.Medium, viewModel.MapDetailLevel);

        viewModel.ResetMapZoom();
        Assert.Equal(1, viewModel.MapZoomLevel);
    }

    [Fact]
    public void MapZoom_ClampsToConfiguredRange()
    {
        using var viewModel = new TsunamiPageViewModel(new StubTsunamiReportRepository([]));

        Assert.Equal(1.0, TsunamiPageViewModel.MinimumMapZoomLevel);
        Assert.Equal(12.0, TsunamiPageViewModel.MaximumMapZoomLevel);

        for (int index = 0; index < 40; index++)
        {
            viewModel.ZoomMapIn();
        }

        Assert.Equal(TsunamiPageViewModel.MaximumMapZoomLevel, viewModel.MapZoomLevel);

        for (int index = 0; index < 40; index++)
        {
            viewModel.ZoomMapOut();
        }

        Assert.Equal(TsunamiPageViewModel.MinimumMapZoomLevel, viewModel.MapZoomLevel);
        Assert.False(viewModel.ZoomMapOut());
    }

    [Fact]
    public void MapZoom_ReportsNoChangeAtConfiguredLimits()
    {
        using var viewModel = new TsunamiPageViewModel(new StubTsunamiReportRepository([]));
        int zoomChangedNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TsunamiPageViewModel.MapZoomLevel))
            {
                zoomChangedNotifications++;
            }
        };

        for (int index = 0; index < 40; index++)
        {
            viewModel.ZoomMapIn();
        }

        Assert.Equal(TsunamiPageViewModel.MaximumMapZoomLevel, viewModel.MapZoomLevel);
        Assert.False(viewModel.ZoomMapIn());
        Assert.Equal(TsunamiPageViewModel.MaximumMapZoomLevel, viewModel.MapZoomLevel);
        Assert.Equal(11, zoomChangedNotifications);

        for (int index = 0; index < 40; index++)
        {
            viewModel.ZoomMapOut();
        }

        Assert.Equal(TsunamiPageViewModel.MinimumMapZoomLevel, viewModel.MapZoomLevel);
        Assert.False(viewModel.ZoomMapOut());
        Assert.Equal(TsunamiPageViewModel.MinimumMapZoomLevel, viewModel.MapZoomLevel);
        Assert.Equal(22, zoomChangedNotifications);
    }

    [Fact]
    public void MapGeometry_IsAvailableBeforeReportSelection()
    {
        using var viewModel = new TsunamiPageViewModel(new StubTsunamiReportRepository([]));

        Assert.False(viewModel.HasSelectedReport);
        Assert.True(viewModel.HasMapGeometry);
        Assert.NotEmpty(viewModel.MapLines);
    }

    [Fact]
    public async Task Timeline_GroupsReportsByEventAndKeepsCancellationAsRelease()
    {
        DateTimeOffset issuedAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9));
        JmaTsunamiReport issued = CreateReport("issued", issuedAt) with
        {
            Items =
            [
                new JmaTsunamiInformationItem(
                    "津波警報",
                    null,
                    null,
                    null,
                    []),
            ],
        };
        JmaTsunamiReport cancelled = CreateReport("cancelled", issuedAt.AddMinutes(10)) with
        {
            Status = ReportStatus.Cancelled,
            Items = issued.Items,
        };
        JmaTsunamiReport otherEvent = CreateReport("other", issuedAt.AddMinutes(20)) with
        {
            EventId = "event-2",
        };
        var repository = new StubTsunamiReportRepository([otherEvent, cancelled, issued]);
        using var viewModel = new TsunamiPageViewModel(repository);

        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectReport(
            issued.EventId,
            issued.Source.SourceId,
            issued.Source.SourceMessageId));
        Assert.False(viewModel.HasReportDifferences);
        Assert.Equal("首报，没有上一报可比较", viewModel.ReportDifferenceStatusText);
        Assert.True(viewModel.SelectReport(
            cancelled.EventId,
            cancelled.Source.SourceId,
            cancelled.Source.SourceMessageId));

        Assert.Equal(
            ["发布", "解除"],
            viewModel.TimelineReports.Select(item => item.StatusText));
        Assert.Equal("津波警報", viewModel.TimelineReports[0].LevelText);
        Assert.True(viewModel.TimelineReports[1].IsCancellation);
        Assert.Equal("解除", viewModel.TimelineReports[1].LevelText);
        Assert.Equal("解除", viewModel.TimelineReports[1].StatusText);
        Assert.Contains(
            viewModel.ReportDifferences,
            item => item.FieldText == "状态" && item.PreviousText == "发布" && item.CurrentText == "取消");
        Assert.Contains(
            viewModel.ReportDifferences,
            item => item.FieldText == "最高等级" && item.PreviousText == "津波警報" && item.CurrentText == "解除");
        Assert.Contains("项变化", viewModel.ReportDifferenceStatusText);
    }

    private static JmaTsunamiReport CreateReport(
        string sourceMessageId,
        DateTimeOffset issuedAt) => new()
        {
            EventId = "event-1",
            ReportCode = "VTSE41",
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Source = new SourceReference(
                "jma-xml-tsunami",
                sourceMessageId),
        };

    private class StubTsunamiReportRepository(
        ImmutableArray<JmaTsunamiReport> reports,
        Exception? loadException = null) : ITsunamiReportRepository
    {
        public ImmutableArray<JmaTsunamiReport> Reports { get; set; } = reports;

        public ImmutableArray<JmaTsunamiReport> ReportsAfterRefresh { get; set; } = [];

        public Exception? LoadException { get; } = loadException;

        public int RefreshCalls { get; private set; }

        public ImmutableArray<SourceStatus> SourceStatuses { get; } =
        [
            new SourceStatus(
                "jma-xml-tsunami",
                SourceConnectionState.Online,
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.FromHours(9))),
        ];

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            Reports = ReportsAfterRefresh;
            return Task.CompletedTask;
        }

        public ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsAsync(
            CancellationToken cancellationToken = default)
        {
            if (LoadException is not null)
            {
                throw LoadException;
            }

            return ValueTask.FromResult(Reports);
        }

        public ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsForEventAsync(
            string eventId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Reports.Where(report => report.EventId == eventId).ToImmutableArray());

        public Task SaveReportsAsync(
            IEnumerable<JmaTsunamiReport> reports,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CatalogStubTsunamiReportRepository(
        ImmutableArray<JmaTsunamiReport> reports,
        JmaTsunamiStationCatalog catalog,
        Exception? catalogLoadException = null) : StubTsunamiReportRepository(reports), ITsunamiStationCatalogRepository
    {
        public Task<JmaTsunamiStationCatalog> LoadStationCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            if (catalogLoadException is not null)
            {
                throw catalogLoadException;
            }

            return Task.FromResult(catalog);
        }

        public Task SaveStationCatalogAsync(
            JmaTsunamiStationCatalog catalog,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
