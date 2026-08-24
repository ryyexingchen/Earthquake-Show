using System.Collections.Immutable;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
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

    private sealed class StubTsunamiReportRepository(
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
}
