using System.Collections.Immutable;

namespace EarthquakeShow.Core.Models;

public enum RealtimeObservationQuality
{
    Unknown,
    Valid,
    Delayed,
    Missing,
    Invalid,
}

public sealed record RealtimeObservationStation(
    string Code,
    string Name,
    GeoCoordinate? Coordinate,
    JmaIntensity Intensity,
    bool IsZero,
    DateTimeOffset SampledAt,
    DateTimeOffset ReceivedAt,
    RealtimeObservationQuality Quality,
    string SourceId,
    string? EventId = null);

public sealed record RealtimeObservationSnapshot(
    ImmutableArray<RealtimeObservationStation> Stations,
    DateTimeOffset SampledAt,
    DateTimeOffset ReceivedAt,
    string SourceId);
