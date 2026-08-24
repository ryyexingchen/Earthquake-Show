using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Abstractions;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed class TsunamiPageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITsunamiReportRepository _repository;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TsunamiPageState _state = new();
    private bool _isDisposed;

    public TsunamiPageViewModel(ITsunamiReportRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TsunamiPageState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
        }
    }

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        State = State with
        {
            LoadState = TsunamiPageLoadState.Loading,
            ErrorMessage = null,
        };

        try
        {
            ImmutableArray<JmaTsunamiReport> reports =
                await _repository.ListReportsAsync(cancellationToken);
            ApplyReports(reports);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            State = State with
            {
                LoadState = TsunamiPageLoadState.Error,
                ErrorMessage = exception.Message,
            };
        }
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            State = State with
            {
                IsRefreshing = true,
                ErrorMessage = null,
            };

            try
            {
                await _repository.RefreshAsync(cancellationToken);
                ImmutableArray<JmaTsunamiReport> reports =
                    await _repository.ListReportsAsync(cancellationToken);
                ApplyReports(reports);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                State = State with
                {
                    LoadState = TsunamiPageLoadState.Error,
                    ErrorMessage = exception.Message,
                };
            }
        }
        finally
        {
            State = State with { IsRefreshing = false };
            _refreshGate.Release();
        }
    }

    public bool SelectReport(
        string eventId,
        string sourceId,
        string sourceMessageId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMessageId);
        JmaTsunamiReport? report = State.Reports.FirstOrDefault(item =>
            string.Equals(item.EventId, eventId, StringComparison.Ordinal) &&
            string.Equals(item.Source.SourceId, sourceId, StringComparison.Ordinal) &&
            string.Equals(item.Source.SourceMessageId, sourceMessageId, StringComparison.Ordinal));
        if (report is null)
        {
            return false;
        }

        State = State with { SelectedReport = report };
        return true;
    }

    public void ClearSelection()
    {
        ThrowIfDisposed();
        State = State with { SelectedReport = null };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _refreshGate.Dispose();
    }

    private void ApplyReports(ImmutableArray<JmaTsunamiReport> reports)
    {
        ImmutableArray<JmaTsunamiReport> ordered = reports
            .OrderByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.ReceivedAt)
            .ThenBy(report => report.EventId, StringComparer.Ordinal)
            .ThenBy(report => report.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(report => report.Source.SourceMessageId, StringComparer.Ordinal)
            .ToImmutableArray();
        JmaTsunamiReport? selected = FindSelectedReport(ordered) ?? ordered.FirstOrDefault();
        ImmutableArray<SourceStatus> statuses = _repository.SourceStatuses;
        State = State with
        {
            Reports = ordered,
            SelectedReport = selected,
            SourceStatuses = statuses,
            LoadState = TsunamiPageLoadState.Ready,
            IsOffline = statuses.IsDefaultOrEmpty ||
                statuses.All(status => status.State != SourceConnectionState.Online),
            ErrorMessage = null,
        };
    }

    private JmaTsunamiReport? FindSelectedReport(
        ImmutableArray<JmaTsunamiReport> reports)
    {
        JmaTsunamiReport? current = State.SelectedReport;
        if (current is null)
        {
            return null;
        }

        return reports.FirstOrDefault(report =>
            string.Equals(report.EventId, current.EventId, StringComparison.Ordinal) &&
            string.Equals(report.Source.SourceId, current.Source.SourceId, StringComparison.Ordinal) &&
            string.Equals(report.Source.SourceMessageId, current.Source.SourceMessageId, StringComparison.Ordinal));
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
