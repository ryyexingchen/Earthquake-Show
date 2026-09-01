using System.Collections.Immutable;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class EarthquakePageViewModelTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 19, 7, 10, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task Load_SelectsNewestEventAndLatestReport()
    {
        EarthquakeReport older = CreateReport("event-old", "old", 1);
        EarthquakeReport newerFirst = CreateReport("event-new", "new-1", 2);
        EarthquakeReport newerLatest = CreateReport("event-new", "new-2", 3);
        using var viewModel = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([older, newerLatest, newerFirst]));

        await viewModel.LoadAsync();

        Assert.Equal(EarthquakePageLoadState.Ready, viewModel.State.LoadState);
        Assert.Equal("event-new", viewModel.State.SelectedEvent?.EventId);
        Assert.Equal("new-2", viewModel.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal("2 条", viewModel.Display.EventCountText);
        Assert.Equal("来源：jma-xml", viewModel.Display.SourceText);
    }

    [Fact]
    public async Task Load_SelectsXmlReportWhenP2pReportIsNewer()
    {
        EarthquakeReport xml = CreateReport("event", "xml", 1);
        EarthquakeReport p2p = CreateReport("event", "p2p", 3) with
        {
            Source = new SourceReference("p2pquake", "p2p"),
        };
        using var viewModel = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository([p2p, xml]));

        await viewModel.LoadAsync();

        Assert.Equal("xml", viewModel.State.ViewedReport?.Source.SourceMessageId);
    }

    [Fact]
    public async Task RepositoryUpdate_NavigatesToNewestIncomingReport()
    {
        EarthquakeReport historyFirst = CreateReport("history", "history-1", 1);
        EarthquakeReport historyLatest = CreateReport("history", "history-2", 2);
        EarthquakeReport current = CreateReport("current", "current-1", 3);
        var repository = new InMemoryEarthquakeEventRepository(
            [historyFirst, historyLatest, current]);
        using var viewModel = new EarthquakePageViewModel(repository);
        int navigationCount = 0;
        viewModel.NewReportNavigationRequested += (_, _) => navigationCount++;
        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectEvent("history"));
        Assert.True(viewModel.SelectReport("jma-xml", "history-1"));

        repository.ApplyReports([
            CreateReport("history", "history-3", 4),
            CreateReport("new-event", "new-event-1", 5),
            CreateReport("newest-event", "newest-event-1", 6),
        ]);

        Assert.Equal("newest-event", viewModel.State.SelectedEvent?.EventId);
        Assert.Equal("newest-event-1", viewModel.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal(1, navigationCount);
    }

    [Fact]
    public async Task RepositoryUpdateWithoutNewReports_PreservesManualReportSelection()
    {
        EarthquakeReport first = CreateReport("event", "event-1", 1);
        EarthquakeReport second = CreateReport("event", "event-2", 2);
        var repository = new InMemoryEarthquakeEventRepository([first, second]);
        using var viewModel = new EarthquakePageViewModel(repository);
        int navigationCount = 0;
        viewModel.NewReportNavigationRequested += (_, _) => navigationCount++;
        await viewModel.LoadAsync();
        Assert.True(viewModel.SelectReport("jma-xml", "event-1"));

        repository.ApplyReports([first, second]);

        Assert.Equal("event-1", viewModel.State.ViewedReport?.Source.SourceMessageId);
        Assert.Equal(0, navigationCount);
    }

    [Fact]
    public async Task SelectEventAndReturnToLatest_UpdateViewedReport()
    {
        var repository = new InMemoryEarthquakeEventRepository([
            CreateReport("event-a", "a-1", 1),
            CreateReport("event-a", "a-2", 2),
            CreateReport("event-b", "b-1", 3),
        ]);
        using var viewModel = new EarthquakePageViewModel(repository);
        await viewModel.LoadAsync();

        Assert.True(viewModel.SelectEvent("event-a"));
        Assert.True(viewModel.SelectReport("jma-xml", "a-1"));
        Assert.Equal("a-1", viewModel.State.ViewedReport?.Source.SourceMessageId);

        viewModel.ReturnToLatestReport();

        Assert.Equal("a-2", viewModel.State.ViewedReport?.Source.SourceMessageId);
        Assert.False(viewModel.SelectEvent("missing"));
    }

    [Fact]
    public async Task Load_RepositoryFailure_StoresErrorState()
    {
        var repository = new TestRepository
        {
            ListException = new InvalidOperationException("测试读取失败"),
        };
        using var viewModel = new EarthquakePageViewModel(repository);

        await viewModel.LoadAsync();

        Assert.Equal(EarthquakePageLoadState.Error, viewModel.State.LoadState);
        Assert.Equal("测试读取失败", viewModel.State.ErrorMessage);
    }

    [Fact]
    public async Task Refresh_DelegatesToRepositoryAndClearsRefreshingState()
    {
        var repository = new TestRepository
        {
            Events = EarthquakeEventMerger.Merge([
                CreateReport("event", "message", 1),
            ]),
        };
        using var viewModel = new EarthquakePageViewModel(repository);

        await viewModel.RefreshAsync();

        Assert.Equal(1, repository.RefreshCount);
        Assert.False(viewModel.State.IsRefreshing);
        Assert.Equal(EarthquakePageLoadState.Ready, viewModel.State.LoadState);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndWaitsForActiveRefresh()
    {
        var repository = new BlockingRepository();
        var viewModel = new EarthquakePageViewModel(repository);

        try
        {
            Task refresh = viewModel.RefreshAsync().AsTask();
            await repository.RefreshStarted.Task;

            Task dispose = viewModel.DisposeAsync().AsTask();

            await dispose;
            await refresh;

            Assert.True(refresh.IsCompletedSuccessfully);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    [Fact]
    public async Task Refresh_SourceStatusProvider_UpdatesOnlineState()
    {
        var status = new SourceStatus(
            "jma-json",
            SourceConnectionState.Online,
            BaseTime,
            BaseTime);
        var repository = new TestRepository
        {
            SourceStatuses = [status],
        };
        using var viewModel = new EarthquakePageViewModel(repository);

        await viewModel.RefreshAsync();

        Assert.Equal(status, Assert.Single(viewModel.State.SourceStatuses));
        Assert.False(viewModel.State.IsOffline);
    }

    [Fact]
    public void PageOptions_AreStoredAsListAndMapState()
    {
        using var viewModel = new EarthquakePageViewModel(new TestRepository());
        var map = new EarthquakeMapViewState
        {
            FocusMode = EarthquakeMapFocusMode.SelectedEvent,
            FollowSelection = false,
        };
        var sourceStatus = new SourceStatus(
            "jma-xml",
            SourceConnectionState.Disconnected,
            BaseTime);

        viewModel.SetSearchText("  熊本  ");
        viewModel.SetSortOrder(EarthquakeEventSortOrder.HighestIntensity);
        viewModel.SetTimeRange(EarthquakeEventTimeRange.Last7Days);
        viewModel.SetMinimumIntensity(JmaIntensity.Four);
        viewModel.SetMinimumMagnitude(5.0);
        viewModel.SetRegionText("  九州  ");
        viewModel.SetSourceId("  jma-json  ");
        viewModel.SetMapViewState(map);
        viewModel.SetSourceState([sourceStatus], isOffline: true);

        Assert.Equal("熊本", viewModel.State.SearchText);
        Assert.Equal(EarthquakeEventSortOrder.HighestIntensity, viewModel.State.SortOrder);
        Assert.Equal(EarthquakeEventTimeRange.Last7Days, viewModel.State.Filters.TimeRange);
        Assert.Equal(JmaIntensity.Four, viewModel.State.Filters.MinimumIntensity);
        Assert.Equal(5.0, viewModel.State.Filters.MinimumMagnitude);
        Assert.Equal("九州", viewModel.State.Filters.RegionText);
        Assert.Equal("jma-json", viewModel.State.Filters.SourceId);
        Assert.Equal(map, viewModel.State.Map);
        Assert.Equal(sourceStatus, Assert.Single(viewModel.State.SourceStatuses));
        Assert.True(viewModel.State.IsOffline);
    }

    private static EarthquakeReport CreateReport(
        string eventId,
        string sourceMessageId,
        int issuedMinute)
    {
        DateTimeOffset issuedAt = BaseTime.AddMinutes(issuedMinute);
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = "VXSE53",
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            IssuedAt = issuedAt,
            ReceivedAt = issuedAt.AddSeconds(1),
            Source = new SourceReference("jma-xml", sourceMessageId),
        };
    }

    private sealed class TestRepository :
        IEarthquakeEventRepository,
        IEarthquakeSourceStatusProvider
    {
        public event EventHandler<EarthquakeEventsChangedEventArgs>? EventsChanged;

        public ImmutableArray<EarthquakeEvent> Events { get; init; } = [];

        public Exception? ListException { get; init; }

        public ImmutableArray<SourceStatus> SourceStatuses { get; init; } = [];

        public int RefreshCount { get; private set; }

        public ValueTask<ImmutableArray<EarthquakeEvent>> ListEventsAsync(
            CancellationToken cancellationToken = default)
        {
            if (ListException is not null)
            {
                throw ListException;
            }

            return ValueTask.FromResult(Events);
        }

        public ValueTask<EarthquakeEvent?> GetEventAsync(
            string eventId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                Events.FirstOrDefault(earthquakeEvent =>
                    earthquakeEvent.EventId == eventId));
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return ValueTask.CompletedTask;
        }

        public void Publish(ImmutableArray<EarthquakeEvent> events)
        {
            EventsChanged?.Invoke(this, new EarthquakeEventsChangedEventArgs(events));
        }
    }

    private sealed class BlockingRepository : IEarthquakeEventRepository
    {
        private readonly TaskCompletionSource _refreshCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<EarthquakeEventsChangedEventArgs>? EventsChanged;

        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ImmutableArray<EarthquakeEvent>> ListEventsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ImmutableArray<EarthquakeEvent>.Empty);

        public ValueTask<EarthquakeEvent?> GetEventAsync(
            string eventId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<EarthquakeEvent?>(null);

        public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshStarted.TrySetResult();
            await _refreshCompletion.Task.WaitAsync(cancellationToken);
        }

        public void Publish(ImmutableArray<EarthquakeEvent> events) =>
            EventsChanged?.Invoke(this, new EarthquakeEventsChangedEventArgs(events));

        public void CompleteRefresh() => _refreshCompletion.TrySetResult();
    }
}
