using System.Collections.Immutable;
using EarthquakeShow.App.ViewModels;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class EarthquakeEventListViewModelTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task Load_DefaultSortsByOriginTimeInsteadOfIssuedTime()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport(
                "issued-later-origin-older",
                3,
                originTime: BaseTime.AddHours(-2)),
            CreateReport(
                "issued-earlier-origin-newer",
                1,
                originTime: BaseTime.AddHours(-1)),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        Assert.Equal(EarthquakeEventSortOrder.LatestOriginTime, list.SortOrder);
        Assert.Equal(
            ["issued-earlier-origin-newer", "issued-later-origin-older"],
            list.Items.Select(item => item.EventId));
        Assert.All(list.Items, item => Assert.False(item.IsNew));
    }

    [Fact]
    public async Task OriginTimeSort_PlacesUnknownTimeAfterKnownTimes()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("unknown-z-older-issued", 0),
            CreateReport("unknown-a-newer-issued", 3),
            CreateReport("older", 1, originTime: BaseTime.AddHours(-2)),
            CreateReport("newer", 2, originTime: BaseTime.AddHours(-1)),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SortOrder = EarthquakeEventSortOrder.LatestOriginTime;

        Assert.Equal(
            ["newer", "older", "unknown-a-newer-issued", "unknown-z-older-issued"],
            list.Items.Select(item => item.EventId));
    }

    [Fact]
    public async Task IntensitySort_PlacesUnknownIntensityAfterKnownIntensities()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("unknown", 3),
            CreateReport("four", 2, intensity: JmaIntensity.Four),
            CreateReport("six", 1, intensity: JmaIntensity.SixLower),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SortOrder = EarthquakeEventSortOrder.HighestIntensity;

        Assert.Equal(
            ["six", "four", "unknown"],
            list.Items.Select(item => item.EventId));
    }

    [Theory]
    [InlineData("event-tokyo")]
    [InlineData("东京湾")]
    [InlineData("CODE-13")]
    [InlineData("VXSE53")]
    [InlineData("jma-json")]
    public async Task Search_MatchesStableEventFields(string searchText)
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport(
                "event-tokyo",
                1,
                hypocenter: new Hypocenter("东京湾", "CODE-13", null, 40),
                sourceId: "jma-json"),
            CreateReport("other", 2, reportCode: "VXSE52"),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SearchText = searchText;

        Assert.Equal("event-tokyo", Assert.Single(list.Items).EventId);
    }

    [Fact]
    public async Task TimeFilter_UsesLatestIssuedTime()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("recent", 0, issuedAt: BaseTime.AddHours(-3)),
            CreateReport("old", 0, issuedAt: BaseTime.AddDays(-2)),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.TimeRange = EarthquakeEventTimeRange.Last24Hours;

        Assert.Equal("recent", Assert.Single(list.Items).EventId);
    }

    [Fact]
    public async Task IntensityFilter_DoesNotTreatUnknownAsZero()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("unknown", 2),
            CreateReport("three", 1, intensity: JmaIntensity.Three),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.MinimumIntensity = JmaIntensity.One;

        Assert.Equal("three", Assert.Single(list.Items).EventId);
    }

    [Fact]
    public async Task MagnitudeFilter_ExcludesUnknownMagnitude()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("unknown", 2),
            CreateReport("large", 1, magnitude: 6.2),
            CreateReport("small", 0, magnitude: 4.8),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.MinimumMagnitude = 5.0;

        Assert.Equal("large", Assert.Single(list.Items).EventId);
    }

    [Fact]
    public async Task DistantVolcanicEruption_IsShownWithUnknownMagnitude()
    {
        EarthquakeReport report = CreateReport(
            "distant-volcano",
            1,
            originTime: BaseTime,
            hypocenter: new Hypocenter("南太平洋", "950", new GeoCoordinate(-15.4, 167.8), null)) with
        {
            ReportType = EarthquakeReportType.DistantEarthquake,
            DistantEarthquakeKind = DistantEarthquakeKind.VolcanicEruption,
        };
        using EarthquakePageViewModel page = await CreatePageAsync([report]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        EarthquakeEventListItemViewModel item = Assert.Single(list.Items);
        Assert.Equal("远地火山喷发 · 发布", item.ReportText);
        Assert.Equal("M 不明", item.MagnitudeText);
    }

    [Fact]
    public async Task RegionFilter_MatchesLatestEffectiveObservationArea()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport(
                "kumamoto",
                1,
                areas: [new IntensityArea("430", "熊本地方", "43", "熊本県", JmaIntensity.Four)]),
            CreateReport("other", 2),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.RegionText = "熊本県";

        Assert.Equal("kumamoto", Assert.Single(list.Items).EventId);
    }

    [Fact]
    public async Task SourceFilter_UsesStableSourceId()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("xml", 1, sourceId: "jma-xml"),
            CreateReport("json", 2, sourceId: "jma-json"),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SourceId = "jma-xml";

        Assert.Equal("xml", Assert.Single(list.Items).EventId);
        list.SourceId = "JMA-XML";
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task CombinedFilters_RequireEveryConditionAndCanBeCleared()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport(
                "match",
                1,
                issuedAt: BaseTime.AddHours(-1),
                hypocenter: new Hypocenter("能登半岛", "ISHIKAWA", null, 10),
                magnitude: 5.8,
                intensity: JmaIntensity.FiveLower,
                sourceId: "jma-xml"),
            CreateReport("other", 2, issuedAt: BaseTime.AddHours(-1), magnitude: 6.0),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SearchText = "match";
        list.TimeRange = EarthquakeEventTimeRange.Last24Hours;
        list.MinimumIntensity = JmaIntensity.Four;
        list.MinimumMagnitude = 5.0;
        list.RegionText = "能登";
        list.SourceId = "jma-xml";

        Assert.Equal("match", Assert.Single(list.Items).EventId);
        Assert.True(list.HasActiveFilters);
        list.ClearFilters();
        Assert.Equal(2, list.Items.Count);
        Assert.False(list.HasActiveFilters);
    }

    [Fact]
    public async Task NoMatchingEvents_ShowsDifferentStateFromEmptyRepository()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("event", 1),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SearchText = "missing";

        Assert.True(list.ShowNoResults);
        Assert.False(list.ShowRepositoryEmpty);
        Assert.Equal("没有匹配的事件", list.EmptyTitle);
        Assert.Equal("0 / 1 条", list.ResultCountText);
    }

    [Fact]
    public async Task SelectingListItem_UpdatesPageSelection()
    {
        using EarthquakePageViewModel page = await CreatePageAsync([
            CreateReport("older", 1),
            CreateReport("newer", 2),
        ]);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        list.SelectedItem = list.Items.Single(item => item.EventId == "older");

        Assert.Equal("older", page.State.SelectedEvent?.EventId);
        Assert.Equal("older", list.SelectedItem?.EventId);
    }

    [Fact]
    public async Task RepositoryUpdate_SelectsNewestEventAndMarksOnlyNewEvent()
    {
        var repository = new InMemoryEarthquakeEventRepository([
            CreateReport("history", 1),
            CreateReport("current", 2),
        ]);
        using var page = new EarthquakePageViewModel(repository);
        await page.LoadAsync();
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);
        IReadOnlyList<EarthquakeEventListItemViewModel> originalItems = list.Items;
        EarthquakeEventListItemViewModel currentItem =
            list.Items.Single(item => item.EventId == "current");
        list.SelectedItem = list.Items.Single(item => item.EventId == "history");

        repository.ApplyReports([CreateReport("new-event", 3)]);

        Assert.Equal("new-event", page.State.SelectedEvent?.EventId);
        Assert.Equal("new-event", list.SelectedItem?.EventId);
        Assert.Same(originalItems, list.Items);
        Assert.Same(currentItem, list.Items.Single(item => item.EventId == "current"));
        Assert.True(list.Items.Single(item => item.EventId == "new-event").IsNew);
        Assert.False(list.Items.Single(item => item.EventId == "current").IsNew);
    }

    [Fact]
    public async Task OneHundredEvents_ProduceStableVirtualizationSource()
    {
        EarthquakeReport[] reports = Enumerable.Range(0, 100)
            .Select(index => CreateReport($"event-{index:000}", index))
            .ToArray();
        using EarthquakePageViewModel page = await CreatePageAsync(reports);
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        Assert.Equal(100, list.Items.Count);
        Assert.Equal("event-099", list.Items[0].EventId);
        Assert.Equal("event-000", list.Items[^1].EventId);
    }

    [Fact]
    public async Task Refresh_PreventsConcurrentRepositoryRefreshes()
    {
        var repository = new BlockingRepository();
        using var page = new EarthquakePageViewModel(repository);
        await page.LoadAsync();
        using var list = new EarthquakeEventListViewModel(page, () => BaseTime);

        Task firstRefresh = list.RefreshAsync().AsTask();
        await repository.RefreshStarted.Task;
        await list.RefreshAsync();

        Assert.Equal(1, repository.RefreshCount);
        repository.CompleteRefresh();
        await firstRefresh;
        Assert.True(list.CanRefresh);
    }

    private static async Task<EarthquakePageViewModel> CreatePageAsync(
        IEnumerable<EarthquakeReport> reports)
    {
        var page = new EarthquakePageViewModel(
            new InMemoryEarthquakeEventRepository(reports));
        await page.LoadAsync();
        return page;
    }

    private static EarthquakeReport CreateReport(
        string eventId,
        int issuedMinute,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? originTime = null,
        Hypocenter? hypocenter = null,
        double? magnitude = null,
        JmaIntensity intensity = JmaIntensity.Unknown,
        string sourceId = "jma-xml",
        string reportCode = "VXSE53",
        ImmutableArray<IntensityArea> areas = default)
    {
        DateTimeOffset actualIssuedAt = issuedAt ?? BaseTime.AddMinutes(issuedMinute);
        return new EarthquakeReport
        {
            EventId = eventId,
            ReportCode = reportCode,
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            OriginTime = originTime,
            IssuedAt = actualIssuedAt,
            ReceivedAt = actualIssuedAt.AddSeconds(1),
            Hypocenter = hypocenter,
            Magnitude = magnitude is null ? null : new Magnitude(magnitude),
            MaxIntensity = intensity,
            IntensityAreas = areas.IsDefault ? [] : areas,
            Source = new SourceReference(sourceId, $"{sourceId}-{eventId}"),
        };
    }

    private sealed class BlockingRepository : IEarthquakeEventRepository
    {
        private readonly TaskCompletionSource _refreshCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<EarthquakeEventsChangedEventArgs>? EventsChanged;

        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RefreshCount { get; private set; }

        public ValueTask<ImmutableArray<EarthquakeEvent>> ListEventsAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(ImmutableArray<EarthquakeEvent>.Empty);
        }

        public ValueTask<EarthquakeEvent?> GetEventAsync(
            string eventId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<EarthquakeEvent?>(null);
        }

        public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            RefreshStarted.SetResult();
            await _refreshCompletion.Task.WaitAsync(cancellationToken);
        }

        public void CompleteRefresh()
        {
            _refreshCompletion.SetResult();
        }

        public void Publish(ImmutableArray<EarthquakeEvent> events)
        {
            EventsChanged?.Invoke(this, new EarthquakeEventsChangedEventArgs(events));
        }
    }
}
