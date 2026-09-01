using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed class EarthquakePageViewModel : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly IEarthquakeEventRepository _repository;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _operationCancellation = new();
    private readonly object _operationSync = new();
    private EarthquakePageState _state = new();
    private EarthquakePageDisplayState _display;
    private int _activeOperationCount;
    private TaskCompletionSource<bool>? _operationsIdle;
    private bool _isDisposed;
    private bool _resourcesDisposed;

    public EarthquakePageViewModel(
        IEarthquakeEventRepository repository,
        SynchronizationContext? synchronizationContext = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        _display = EarthquakePageDisplayState.Create(_state);
        _repository.EventsChanged += OnRepositoryEventsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NewReportNavigationRequested;

    public EarthquakePageState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            _display = EarthquakePageDisplayState.Create(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public EarthquakePageDisplayState Display => _display;

    public ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BeginOperation();
        return new ValueTask(ExecuteSerializedAsync(LoadCoreAsync, cancellationToken));
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        State = State with
        {
            LoadState = EarthquakePageLoadState.Loading,
            ErrorMessage = null,
        };

        try
        {
            ImmutableArray<EarthquakeEvent> events =
                await _repository.ListEventsAsync(cancellationToken);
            ApplyEvents(events);
            ApplyRepositorySourceState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            State = State with
            {
                LoadState = EarthquakePageLoadState.Error,
                ErrorMessage = exception.Message,
            };
        }
    }

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BeginOperation();
        return new ValueTask(ExecuteSerializedAsync(RefreshCoreAsync, cancellationToken));
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        State = State with
        {
            IsRefreshing = true,
            ErrorMessage = null,
        };

        try
        {
            await _repository.RefreshAsync(cancellationToken);
            ImmutableArray<EarthquakeEvent> events =
                await _repository.ListEventsAsync(cancellationToken);
            ApplyEvents(events, navigateToNewest: true);
            ApplyRepositorySourceState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            State = State with
            {
                LoadState = EarthquakePageLoadState.Error,
                ErrorMessage = exception.Message,
            };
        }
        finally
        {
            State = State with { IsRefreshing = false };
        }
    }

    public bool SelectEvent(string eventId)
    {
        ThrowIfDisposed();
        EarthquakeEvent? selectedEvent = State.Events.FirstOrDefault(
            item => string.Equals(item.EventId, eventId, StringComparison.Ordinal));
        if (selectedEvent is null)
        {
            return false;
        }

        State = State with
        {
            SelectedEvent = selectedEvent,
            ViewedReport = selectedEvent.PreferredReport,
        };
        return true;
    }

    public bool SelectReport(string sourceId, string sourceMessageId)
    {
        ThrowIfDisposed();
        EarthquakeReport? report = State.SelectedEvent?.Reports.FirstOrDefault(
            item => string.Equals(item.Source.SourceId, sourceId, StringComparison.Ordinal) &&
                string.Equals(
                    item.Source.SourceMessageId,
                    sourceMessageId,
                    StringComparison.Ordinal));
        if (report is null)
        {
            return false;
        }

        State = State with { ViewedReport = report };
        return true;
    }

    public void ReturnToLatestReport()
    {
        ThrowIfDisposed();
        State = State with { ViewedReport = State.SelectedEvent?.PreferredReport };
    }

    public void SetSearchText(string? searchText)
    {
        ThrowIfDisposed();
        State = State with { SearchText = searchText?.Trim() ?? string.Empty };
    }

    public void SetSortOrder(EarthquakeEventSortOrder sortOrder)
    {
        ThrowIfDisposed();
        State = State with { SortOrder = sortOrder };
    }

    public void SetTimeRange(EarthquakeEventTimeRange timeRange)
    {
        ThrowIfDisposed();
        State = State with { Filters = State.Filters with { TimeRange = timeRange } };
    }

    public void SetMinimumIntensity(JmaIntensity minimumIntensity)
    {
        ThrowIfDisposed();
        State = State with
        {
            Filters = State.Filters with { MinimumIntensity = minimumIntensity },
        };
    }

    public void SetMinimumMagnitude(double? minimumMagnitude)
    {
        ThrowIfDisposed();
        if (minimumMagnitude is double value && !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumMagnitude),
                minimumMagnitude,
                "最低震级必须是有限值。");
        }

        State = State with
        {
            Filters = State.Filters with { MinimumMagnitude = minimumMagnitude },
        };
    }

    public void SetRegionText(string? regionText)
    {
        ThrowIfDisposed();
        State = State with
        {
            Filters = State.Filters with { RegionText = regionText?.Trim() ?? string.Empty },
        };
    }

    public void SetSourceId(string? sourceId)
    {
        ThrowIfDisposed();
        State = State with
        {
            Filters = State.Filters with { SourceId = sourceId?.Trim() ?? string.Empty },
        };
    }

    public void ClearFilters()
    {
        ThrowIfDisposed();
        State = State with { Filters = new EarthquakeEventFilterState() };
    }

    public void SetMapViewState(EarthquakeMapViewState mapViewState)
    {
        ThrowIfDisposed();
        State = State with
        {
            Map = mapViewState ?? throw new ArgumentNullException(nameof(mapViewState)),
        };
    }

    public void SetSourceState(IEnumerable<SourceStatus> statuses, bool isOffline)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(statuses);
        State = State with
        {
            SourceStatuses = statuses.ToImmutableArray(),
            IsOffline = isOffline,
        };
    }

    public void UpdateDisplayClock(DateTimeOffset now)
    {
        ThrowIfDisposed();
        EarthquakePageDisplayState display = EarthquakePageDisplayState.Create(State, now);
        if (_display == display)
        {
            return;
        }

        _display = display;
        OnPropertyChanged(nameof(Display));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _operationCancellation.Cancel();
        }

        await WaitForOperationsAsync().ConfigureAwait(true);
        DisposeResources();
    }

    public void Dispose()
    {
        if (_isDisposed && _resourcesDisposed)
        {
            return;
        }

        _isDisposed = true;
        _operationCancellation.Cancel();
        if (Volatile.Read(ref _activeOperationCount) == 0)
        {
            DisposeResources();
        }
    }

    private async Task ExecuteSerializedAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _operationCancellation.Token);
        bool entered = false;
        try
        {
            await _refreshGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(true);
            entered = true;
            await operation(linkedCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_isDisposed)
        {
        }
        finally
        {
            if (entered)
            {
                _refreshGate.Release();
            }

            EndOperation();
        }
    }

    private void BeginOperation()
    {
        lock (_operationSync)
        {
            if (_activeOperationCount++ == 0)
            {
                _operationsIdle = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_operationSync)
        {
            _activeOperationCount--;
            if (_activeOperationCount == 0)
            {
                idle = _operationsIdle;
                _operationsIdle = null;
            }
        }

        idle?.TrySetResult(true);
        if (_isDisposed && Volatile.Read(ref _activeOperationCount) == 0)
        {
            DisposeResources();
        }
    }

    private Task WaitForOperationsAsync()
    {
        lock (_operationSync)
        {
            return _activeOperationCount == 0
                ? Task.CompletedTask
                : _operationsIdle!.Task;
        }
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        _repository.EventsChanged -= OnRepositoryEventsChanged;
        _refreshGate.Dispose();
        _operationCancellation.Dispose();
    }

    private void OnRepositoryEventsChanged(
        object? sender,
        EarthquakeEventsChangedEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_synchronizationContext is not null &&
            SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                _ => ApplyRepositorySnapshot(eventArgs.Events),
                null);
            return;
        }

        ApplyRepositorySnapshot(eventArgs.Events);
    }

    private void ApplyRepositorySnapshot(ImmutableArray<EarthquakeEvent> events)
    {
        ApplyEvents(events, navigateToNewest: true);
        ApplyRepositorySourceState();
    }

    private void ApplyEvents(
        ImmutableArray<EarthquakeEvent> events,
        bool navigateToNewest = false)
    {
        EarthquakeEvent? selectedEvent = FindSelectedEvent(events);
        EarthquakeReport? viewedReport = FindViewedReport(selectedEvent);
        bool navigatedToNewest = false;
        if (navigateToNewest &&
            TryFindNewestIncomingReport(events, State.Events, out EarthquakeEvent? incomingEvent,
                out EarthquakeReport? incomingReport))
        {
            selectedEvent = incomingEvent;
            viewedReport = incomingReport;
            navigatedToNewest = true;
        }

        State = State with
        {
            Events = events,
            SelectedEvent = selectedEvent,
            ViewedReport = viewedReport,
            LoadState = EarthquakePageLoadState.Ready,
            ErrorMessage = null,
        };

        if (navigatedToNewest)
        {
            NewReportNavigationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool TryFindNewestIncomingReport(
        ImmutableArray<EarthquakeEvent> events,
        ImmutableArray<EarthquakeEvent> previousEvents,
        out EarthquakeEvent? eventResult,
        out EarthquakeReport? reportResult)
    {
        HashSet<string> previousReportKeys = previousEvents
            .SelectMany(item => item.Reports.Select(report =>
                GetReportKey(item.EventId, report)))
            .ToHashSet(StringComparer.Ordinal);
        (EarthquakeEvent Event, EarthquakeReport Report)? newest = events
            .SelectMany(item => item.Reports.Select(report => (Event: item, Report: report)))
            .Where(item => !previousReportKeys.Contains(
                GetReportKey(item.Event.EventId, item.Report)))
            .OrderByDescending(item => item.Report.IssuedAt)
            .ThenByDescending(item => item.Report.ReceivedAt)
            .ThenByDescending(item => string.Equals(
                item.Report.Source.SourceId,
                "jma-xml",
                StringComparison.Ordinal))
            .ThenBy(item => item.Event.EventId, StringComparer.Ordinal)
            .Select(item => ((EarthquakeEvent Event, EarthquakeReport Report)?)item)
            .FirstOrDefault();
        if (newest is null)
        {
            eventResult = null;
            reportResult = null;
            return false;
        }

        eventResult = newest.Value.Event;
        reportResult = newest.Value.Report;
        return true;
    }

    private static string GetReportKey(string eventId, EarthquakeReport report)
    {
        return string.Join(
            '\u001f',
            eventId,
            report.Source.SourceId,
            report.Source.SourceMessageId);
    }

    private void ApplyRepositorySourceState()
    {
        if (_repository is not IEarthquakeSourceStatusProvider provider ||
            provider.SourceStatuses.IsDefaultOrEmpty)
        {
            return;
        }

        ImmutableArray<SourceStatus> statuses = provider.SourceStatuses;
        SetSourceState(
            statuses,
            statuses.All(status => status.State != SourceConnectionState.Online));
    }

    private EarthquakeEvent? FindSelectedEvent(ImmutableArray<EarthquakeEvent> events)
    {
        string? selectedEventId = State.SelectedEvent?.EventId;
        return events.FirstOrDefault(item =>
                string.Equals(item.EventId, selectedEventId, StringComparison.Ordinal))
            ?? events.FirstOrDefault();
    }

    private EarthquakeReport? FindViewedReport(EarthquakeEvent? selectedEvent)
    {
        if (selectedEvent is null)
        {
            return null;
        }

        SourceReference? viewedSource = State.ViewedReport?.Source;
        return selectedEvent.Reports.FirstOrDefault(report =>
                string.Equals(
                    report.Source.SourceId,
                    viewedSource?.SourceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    report.Source.SourceMessageId,
                    viewedSource?.SourceMessageId,
                    StringComparison.Ordinal))
            ?? selectedEvent.PreferredReport;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
