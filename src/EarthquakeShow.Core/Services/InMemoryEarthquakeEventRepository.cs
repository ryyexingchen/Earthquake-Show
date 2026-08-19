using System.Collections.Immutable;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public sealed class InMemoryEarthquakeEventRepository : IEarthquakeEventRepository
{
    private readonly object _syncRoot = new();
    private ImmutableArray<EarthquakeReport> _reports;
    private ImmutableArray<EarthquakeEvent> _events;

    public InMemoryEarthquakeEventRepository(IEnumerable<EarthquakeReport>? initialReports = null)
    {
        _reports = initialReports?.ToImmutableArray() ?? [];
        _events = EarthquakeEventMerger.Merge(_reports);
    }

    public event EventHandler<EarthquakeEventsChangedEventArgs>? EventsChanged;

    public ValueTask<ImmutableArray<EarthquakeEvent>> ListEventsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            return ValueTask.FromResult(_events);
        }
    }

    public ValueTask<EarthquakeEvent?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            EarthquakeEvent? earthquakeEvent = _events.FirstOrDefault(
                item => string.Equals(item.EventId, eventId, StringComparison.Ordinal));
            return ValueTask.FromResult(earthquakeEvent);
        }
    }

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public void ApplyReports(IEnumerable<EarthquakeReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ImmutableArray<EarthquakeReport> incomingReports = reports.ToImmutableArray();
        ImmutableArray<EarthquakeEvent> events;

        lock (_syncRoot)
        {
            _reports = _reports.AddRange(incomingReports);
            _events = EarthquakeEventMerger.Merge(_reports);
            events = _events;
        }

        EventsChanged?.Invoke(this, new EarthquakeEventsChangedEventArgs(events));
    }
}
