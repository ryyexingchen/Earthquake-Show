using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Abstractions;

public interface IEarthquakeEventRepository
{
    event EventHandler<EarthquakeEventsChangedEventArgs>? EventsChanged;

    ValueTask<ImmutableArray<EarthquakeEvent>> ListEventsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<EarthquakeEvent?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    ValueTask RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class EarthquakeEventsChangedEventArgs(
    ImmutableArray<EarthquakeEvent> events) : EventArgs
{
    public ImmutableArray<EarthquakeEvent> Events { get; } = events;
}
