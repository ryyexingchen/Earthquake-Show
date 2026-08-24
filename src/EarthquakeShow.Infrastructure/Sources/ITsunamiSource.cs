using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public interface IRealtimeTsunamiSource
{
    string SourceId { get; }

    Task<TsunamiSourceFetchResult> FetchAsync(
        CancellationToken cancellationToken = default);
}

public interface IIncrementalTsunamiSource
{
    Task<TsunamiSourceFetchResult> FetchSinceAsync(
        DateTimeOffset? since,
        CancellationToken cancellationToken = default);
}

public sealed record TsunamiSourceFetchResult(
    ImmutableArray<JmaTsunamiReport> Reports,
    SourceStatus Status);
