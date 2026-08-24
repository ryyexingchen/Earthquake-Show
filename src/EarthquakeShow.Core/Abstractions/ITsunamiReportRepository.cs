using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Abstractions;

/// <summary>
/// 独立海啸报文的持久化边界，不把海啸报文混入地震事件仓储。
/// </summary>
public interface ITsunamiReportRepository
{
    ImmutableArray<SourceStatus> SourceStatuses { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ImmutableArray<JmaTsunamiReport>> ListReportsForEventAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task SaveReportsAsync(
        IEnumerable<JmaTsunamiReport> reports,
        CancellationToken cancellationToken = default);
}
