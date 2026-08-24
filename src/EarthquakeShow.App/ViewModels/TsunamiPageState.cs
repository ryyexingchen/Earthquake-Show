using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public enum TsunamiPageLoadState
{
    NotLoaded,
    Loading,
    Ready,
    Error,
}

public sealed record TsunamiPageState
{
    public ImmutableArray<JmaTsunamiReport> Reports { get; init; } = [];

    public JmaTsunamiReport? SelectedReport { get; init; }

    public ImmutableArray<SourceStatus> SourceStatuses { get; init; } = [];

    public TsunamiPageLoadState LoadState { get; init; }

    public bool IsRefreshing { get; init; }

    public bool IsOffline { get; init; }

    public string? ErrorMessage { get; init; }
}
