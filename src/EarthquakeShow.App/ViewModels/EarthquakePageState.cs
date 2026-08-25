using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public enum EarthquakePageLoadState
{
    NotLoaded,
    Loading,
    Ready,
    Error,
}

public enum EarthquakeEventSortOrder
{
    LatestIssued,
    LatestOriginTime,
    HighestIntensity,
}

public enum EarthquakeEventTimeRange
{
    All,
    Last24Hours,
    Last7Days,
    Last30Days,
}

public enum EarthquakeMapFocusMode
{
    JapanOverview,
    SelectedEvent,
}

public sealed record EarthquakeMapViewState
{
    public EarthquakeMapFocusMode FocusMode { get; init; } =
        EarthquakeMapFocusMode.JapanOverview;

    public bool FollowSelection { get; init; } = true;
}

public sealed record EarthquakeEventFilterState
{
    public EarthquakeEventTimeRange TimeRange { get; init; }

    public JmaIntensity MinimumIntensity { get; init; } = JmaIntensity.Unknown;

    public double? MinimumMagnitude { get; init; }

    public string RegionText { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public bool IsActive =>
        TimeRange != EarthquakeEventTimeRange.All ||
        MinimumIntensity != JmaIntensity.Unknown ||
        MinimumMagnitude is not null ||
        RegionText.Length > 0 ||
        SourceId.Length > 0;
}

public sealed record EarthquakePageState
{
    public ImmutableArray<EarthquakeEvent> Events { get; init; } = [];

    public EarthquakeEvent? SelectedEvent { get; init; }

    public EarthquakeReport? ViewedReport { get; init; }

    public string SearchText { get; init; } = string.Empty;

    public EarthquakeEventSortOrder SortOrder { get; init; } =
        EarthquakeEventSortOrder.LatestOriginTime;

    public EarthquakeEventFilterState Filters { get; init; } = new();

    public EarthquakeMapViewState Map { get; init; } = new();

    public ImmutableArray<SourceStatus> SourceStatuses { get; init; } = [];

    public EarthquakePageLoadState LoadState { get; init; }

    public bool IsRefreshing { get; init; }

    public bool IsOffline { get; init; }

    public string? ErrorMessage { get; init; }
}
