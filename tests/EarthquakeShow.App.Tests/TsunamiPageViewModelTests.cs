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
