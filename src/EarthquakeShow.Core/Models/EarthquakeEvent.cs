using System.Collections.Immutable;

namespace EarthquakeShow.Core.Models;

public sealed record EarthquakeEvent
{
    public required string EventId { get; init; }

    public ImmutableArray<EarthquakeReport> Reports { get; init; } = [];
}
