using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public interface IRealtimeEarthquakeSource
{
    string SourceId { get; }

    Task<EarthquakeSourceFetchResult> FetchAsync(
        CancellationToken cancellationToken = default);
}

public sealed record EarthquakeSourceFetchResult(
    ImmutableArray<EarthquakeReport> Reports,
    SourceStatus Status);
