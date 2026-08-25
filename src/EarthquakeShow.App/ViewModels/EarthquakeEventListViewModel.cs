using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record EventListOption<T>(T Value, string Label);

public sealed class EarthquakeEventListViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EarthquakePageViewModel _page;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _newEventIds = new(StringComparer.Ordinal);
    private HashSet<string> _knownEventIds = new(StringComparer.Ordinal);
    private readonly ObservableCollection<EarthquakeEventListItemViewModel> _items = [];
    private IReadOnlyList<EventListOption<string>> _sourceOptions = [];
    private EarthquakeEventListItemViewModel? _selectedItem;
    private bool _hasObservedLoadedSnapshot;
    private bool _isRefreshPending;
    private bool _isDisposed;

    public EarthquakeEventListViewModel(
        EarthquakePageViewModel page,
        Func<DateTimeOffset>? now = null)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _now = now ?? (() => DateTimeOffset.Now);
        _page.PropertyChanged += OnPagePropertyChanged;
        RebuildItems();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static IReadOnlyList<EventListOption<EarthquakeEventSortOrder>> SortOptions { get; } =
    [
        new(EarthquakeEventSortOrder.LatestOriginTime, "最新发生"),
        new(EarthquakeEventSortOrder.LatestIssued, "最新发布"),
        new(EarthquakeEventSortOrder.HighestIntensity, "最大震度"),
    ];

    public static IReadOnlyList<EventListOption<EarthquakeEventTimeRange>> TimeRangeOptions { get; } =
    [
        new(EarthquakeEventTimeRange.All, "全部时间"),
        new(EarthquakeEventTimeRange.Last24Hours, "最近 24 小时"),
        new(EarthquakeEventTimeRange.Last7Days, "最近 7 天"),
        new(EarthquakeEventTimeRange.Last30Days, "最近 30 天"),
    ];

    public static IReadOnlyList<EventListOption<JmaIntensity>> IntensityOptions { get; } =
    [
        new(JmaIntensity.Unknown, "不限震度"),
        new(JmaIntensity.One, "震度 1+"),
        new(JmaIntensity.Two, "震度 2+"),
        new(JmaIntensity.Three, "震度 3+"),
        new(JmaIntensity.Four, "震度 4+"),
        new(JmaIntensity.FiveLower, "震度 5弱+"),
        new(JmaIntensity.FiveUpper, "震度 5强+"),
        new(JmaIntensity.SixLower, "震度 6弱+"),
        new(JmaIntensity.SixUpper, "震度 6强+"),
        new(JmaIntensity.Seven, "震度 7"),
    ];

    public static IReadOnlyList<EventListOption<double?>> MagnitudeOptions { get; } =
    [
        new(null, "不限震级"),
        new(3.0, "M 3.0+"),
        new(4.0, "M 4.0+"),
        new(5.0, "M 5.0+"),
        new(6.0, "M 6.0+"),
        new(7.0, "M 7.0+"),
    ];

    public IReadOnlyList<EarthquakeEventListItemViewModel> Items => _items;

    public IReadOnlyList<EventListOption<string>> SourceOptions => _sourceOptions;

    public EarthquakeEventListItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value)
            {
                return;
            }

            _selectedItem = value;
            OnPropertyChanged();
            if (value is not null)
            {
                _newEventIds.Remove(value.EventId);
                _page.SelectEvent(value.EventId);
            }
        }
    }

    public string SearchText
    {
        get => _page.State.SearchText;
        set => _page.SetSearchText(value);
    }

    public EarthquakeEventSortOrder SortOrder
    {
        get => _page.State.SortOrder;
        set => _page.SetSortOrder(value);
    }

    public EarthquakeEventTimeRange TimeRange
    {
        get => _page.State.Filters.TimeRange;
        set => _page.SetTimeRange(value);
    }

    public JmaIntensity MinimumIntensity
    {
        get => _page.State.Filters.MinimumIntensity;
        set => _page.SetMinimumIntensity(value);
    }

    public double? MinimumMagnitude
    {
        get => _page.State.Filters.MinimumMagnitude;
        set => _page.SetMinimumMagnitude(value);
    }

    public string RegionText
    {
        get => _page.State.Filters.RegionText;
        set => _page.SetRegionText(value);
    }

    public string SourceId
    {
        get => _page.State.Filters.SourceId;
        set => _page.SetSourceId(value);
    }

    public bool IsLoading =>
        _page.State.LoadState == EarthquakePageLoadState.Loading &&
        _page.State.Events.IsDefaultOrEmpty;

    public bool ShowErrorState =>
        _page.State.LoadState == EarthquakePageLoadState.Error &&
        _page.State.Events.IsDefaultOrEmpty;

    public bool ShowRepositoryEmpty =>
        !IsLoading && !ShowErrorState && _page.State.Events.IsDefaultOrEmpty;

    public bool ShowNoResults =>
        !_page.State.Events.IsDefaultOrEmpty && _items.Count == 0;

    public bool HasResults => _items.Count > 0;

    public bool HasActiveFilters =>
        _page.State.SearchText.Length > 0 || _page.State.Filters.IsActive;

    public bool CanRefresh => !_isRefreshPending && !_page.State.IsRefreshing;

    public string ResultCountText => HasActiveFilters
        ? $"{_items.Count} / {_page.State.Events.Length} 条"
        : $"{_page.State.Events.Length} 条";

    public string EmptyTitle => ShowErrorState
        ? "事件读取失败"
        : ShowNoResults ? "没有匹配的事件" : "暂无地震事件";

    public string EmptyMessage => ShowErrorState
        ? _page.State.ErrorMessage ?? "未知错误"
        : ShowNoResults ? "请调整搜索或筛选条件" : "本地缓存中没有事件";

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanRefresh)
        {
            return;
        }

        _isRefreshPending = true;
        RaiseRefreshProperties();
        try
        {
            await _page.RefreshAsync(cancellationToken);
        }
        finally
        {
            _isRefreshPending = false;
            RaiseRefreshProperties();
        }
    }

    public void ClearFilters()
    {
        ThrowIfDisposed();
        _page.SetSearchText(string.Empty);
        _page.ClearFilters();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _page.PropertyChanged -= OnPagePropertyChanged;
        _isDisposed = true;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(EarthquakePageViewModel.State))
        {
            RebuildItems();
        }
    }

    private void RebuildItems()
    {
        EarthquakePageState state = _page.State;
        TrackNewEvents(state);

        EarthquakeEventListItemViewModel[] items = ApplySort(
                state.Events.Where(MatchesFilters),
                state.SortOrder)
            .Select(item => EarthquakeEventListItemViewModel.Create(
                item,
                _newEventIds.Contains(item.EventId)))
            .ToArray();
        SynchronizeItems(items);

        string? selectedEventId = state.SelectedEvent?.EventId;
        _selectedItem = _items.FirstOrDefault(item =>
            string.Equals(item.EventId, selectedEventId, StringComparison.Ordinal));
        _sourceOptions = BuildSourceOptions(state);

        OnPropertyChanged(nameof(SourceOptions));
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SortOrder));
        OnPropertyChanged(nameof(TimeRange));
        OnPropertyChanged(nameof(MinimumIntensity));
        OnPropertyChanged(nameof(MinimumMagnitude));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(SourceId));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowRepositoryEmpty));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        RaiseRefreshProperties();
    }

    private void TrackNewEvents(EarthquakePageState state)
    {
        var currentIds = state.Events
            .Select(item => item.EventId)
            .ToHashSet(StringComparer.Ordinal);
        if (_hasObservedLoadedSnapshot)
        {
            _newEventIds.UnionWith(currentIds.Except(_knownEventIds));
        }
        else if (state.LoadState == EarthquakePageLoadState.Ready)
        {
            _hasObservedLoadedSnapshot = true;
        }

        _newEventIds.IntersectWith(currentIds);
        _knownEventIds = currentIds;
    }

    private bool MatchesFilters(EarthquakeEvent earthquakeEvent)
    {
        EarthquakePageState state = _page.State;
        EarthquakeEventSummary? summary = earthquakeEvent.Summary;
        if (summary is null)
        {
            return false;
        }

        return MatchesSearch(earthquakeEvent, state.SearchText) &&
            MatchesTime(summary.UpdatedAt, state.Filters.TimeRange) &&
            MatchesIntensity(summary.MaxIntensity, state.Filters.MinimumIntensity) &&
            MatchesMagnitude(summary.Magnitude, state.Filters.MinimumMagnitude) &&
            MatchesRegion(earthquakeEvent, state.Filters.RegionText) &&
            MatchesSource(earthquakeEvent, state.Filters.SourceId);
    }

    private static bool MatchesSearch(EarthquakeEvent earthquakeEvent, string searchText)
    {
        if (searchText.Length == 0)
        {
            return true;
        }

        EarthquakeEventSummary? summary = earthquakeEvent.Summary;
        return Contains(earthquakeEvent.EventId, searchText) ||
            Contains(summary?.Hypocenter?.Name, searchText) ||
            Contains(summary?.Hypocenter?.Code, searchText) ||
            earthquakeEvent.Reports.Any(report =>
                Contains(report.ReportCode, searchText) ||
                Contains(report.Source.SourceId, searchText));
    }

    private bool MatchesTime(DateTimeOffset updatedAt, EarthquakeEventTimeRange timeRange)
    {
        TimeSpan? range = timeRange switch
        {
            EarthquakeEventTimeRange.All => null,
            EarthquakeEventTimeRange.Last24Hours => TimeSpan.FromHours(24),
            EarthquakeEventTimeRange.Last7Days => TimeSpan.FromDays(7),
            EarthquakeEventTimeRange.Last30Days => TimeSpan.FromDays(30),
            _ => throw new ArgumentOutOfRangeException(nameof(timeRange), timeRange, null),
        };
        return range is null || updatedAt >= _now() - range.Value;
    }

    private static bool MatchesIntensity(
        JmaIntensity intensity,
        JmaIntensity minimumIntensity)
    {
        return minimumIntensity == JmaIntensity.Unknown ||
            intensity != JmaIntensity.Unknown && intensity >= minimumIntensity;
    }

    private static bool MatchesMagnitude(Magnitude? magnitude, double? minimumMagnitude)
    {
        return minimumMagnitude is null ||
            magnitude?.Value is double value && value >= minimumMagnitude.Value;
    }

    private static bool MatchesRegion(EarthquakeEvent earthquakeEvent, string regionText)
    {
        if (regionText.Length == 0)
        {
            return true;
        }

        EarthquakeEventSummary? summary = earthquakeEvent.Summary;
        if (Contains(summary?.Hypocenter?.Name, regionText) ||
            Contains(summary?.Hypocenter?.Code, regionText))
        {
            return true;
        }

        EarthquakeReport? report = earthquakeEvent.LatestEffectiveReport;
        return report is not null &&
            (report.IntensityAreas.Any(area =>
                Contains(area.Name, regionText) || Contains(area.Code, regionText) ||
                Contains(area.PrefectureName, regionText) ||
                Contains(area.PrefectureCode, regionText)) ||
            report.IntensityMunicipalities.Any(municipality =>
                Contains(municipality.Name, regionText) ||
                Contains(municipality.Code, regionText)) ||
            report.IntensityStations.Any(station =>
                Contains(station.Name, regionText) || Contains(station.Code, regionText)));
    }

    private static bool MatchesSource(EarthquakeEvent earthquakeEvent, string sourceId)
    {
        return sourceId.Length == 0 || earthquakeEvent.Reports.Any(report =>
            string.Equals(
                report.Source.SourceId,
                sourceId,
                StringComparison.Ordinal));
    }

    private static IOrderedEnumerable<EarthquakeEvent> ApplySort(
        IEnumerable<EarthquakeEvent> events,
        EarthquakeEventSortOrder sortOrder)
    {
        return sortOrder switch
        {
            EarthquakeEventSortOrder.LatestIssued => events
                .OrderByDescending(item => item.Summary?.UpdatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal),
            EarthquakeEventSortOrder.LatestOriginTime => events
                .OrderBy(item => item.Summary?.OriginTime is null)
                .ThenByDescending(item => item.Summary?.OriginTime)
                .ThenByDescending(item => item.Summary?.UpdatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal),
            EarthquakeEventSortOrder.HighestIntensity => events
                .OrderBy(item => item.Summary?.MaxIntensity == JmaIntensity.Unknown)
                .ThenByDescending(item => item.Summary?.MaxIntensity)
                .ThenByDescending(item => item.Summary?.UpdatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder, null),
        };
    }

    private static IReadOnlyList<EventListOption<string>> BuildSourceOptions(
        EarthquakePageState state)
    {
        var options = new List<EventListOption<string>>
        {
            new(string.Empty, "全部来源"),
        };
        options.AddRange(state.Events
            .SelectMany(item => item.Reports)
            .Select(report => report.Source.SourceId)
            .Append(state.Filters.SourceId)
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(sourceId => sourceId, StringComparer.Ordinal)
            .Select(sourceId => new EventListOption<string>(sourceId, sourceId)));
        return options;
    }

    private void SynchronizeItems(IReadOnlyList<EarthquakeEventListItemViewModel> desiredItems)
    {
        for (int targetIndex = 0; targetIndex < desiredItems.Count; targetIndex++)
        {
            EarthquakeEventListItemViewModel desiredItem = desiredItems[targetIndex];
            int existingIndex = FindItemIndex(desiredItem.EventId, targetIndex);
            if (existingIndex < 0)
            {
                _items.Insert(targetIndex, desiredItem);
                continue;
            }

            if (existingIndex != targetIndex)
            {
                _items.Move(existingIndex, targetIndex);
            }

            if (_items[targetIndex] != desiredItem)
            {
                _items[targetIndex] = desiredItem;
            }
        }

        while (_items.Count > desiredItems.Count)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }

    private int FindItemIndex(string eventId, int startIndex)
    {
        for (int index = startIndex; index < _items.Count; index++)
        {
            if (string.Equals(_items[index].EventId, eventId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Contains(string? value, string searchText)
    {
        return value?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void RaiseRefreshProperties()
    {
        OnPropertyChanged(nameof(CanRefresh));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
