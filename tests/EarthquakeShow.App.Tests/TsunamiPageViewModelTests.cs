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
    }

    [Fact]
    public void MapZoom_UsesOverviewAtLowZoomAndDetailedAfterThreshold()
    {
        using var viewModel = new TsunamiPageViewModel(new StubTsunamiReportRepository([]));

        Assert.Equal(TsunamiMapDetailLevel.Overview, viewModel.MapDetailLevel);
        Assert.Equal("低精度地图 · 1.0×", viewModel.MapStatusText);

        viewModel.ZoomMapIn();
        Assert.Equal(1.5, viewModel.MapZoomLevel);
        Assert.Equal(TsunamiMapDetailLevel.Overview, viewModel.MapDetailLevel);

        viewModel.ZoomMapIn();
        viewModel.ZoomMapIn();
        Assert.Equal(2.5, viewModel.MapZoomLevel);
        Assert.Equal(TsunamiMapDetailLevel.Detailed, viewModel.MapDetailLevel);

        viewModel.ResetMapZoom();
        Assert.Equal(1, viewModel.MapZoomLevel);
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
