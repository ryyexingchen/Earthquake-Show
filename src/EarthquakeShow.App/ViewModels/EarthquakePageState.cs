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

public sealed record EarthquakePageState
{
    public ImmutableArray<EarthquakeEvent> Events { get; init; } = [];

    public EarthquakeEvent? SelectedEvent { get; init; }

    public EarthquakeReport? ViewedReport { get; init; }

    public string SearchText { get; init; } = string.Empty;

    public EarthquakeEventSortOrder SortOrder { get; init; } =
        EarthquakeEventSortOrder.LatestIssued;

    public EarthquakeMapViewState Map { get; init; } = new();

    public ImmutableArray<SourceStatus> SourceStatuses { get; init; } = [];

    public EarthquakePageLoadState LoadState { get; init; }

    public bool IsRefreshing { get; init; }

    public bool IsOffline { get; init; }

    public string? ErrorMessage { get; init; }
}
