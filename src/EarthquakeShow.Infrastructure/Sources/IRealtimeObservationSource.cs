using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public interface IRealtimeObservationSource
{
    string SourceId { get; }

    Task<RealtimeObservationFetchResult> FetchAsync(
        CancellationToken cancellationToken = default);
}

public sealed record RealtimeObservationFetchResult(
    ImmutableArray<RealtimeObservationStation> Stations,
    SourceStatus Status);
