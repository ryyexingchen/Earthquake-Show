using System.Collections.Immutable;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Infrastructure.Sources;

public sealed class ReconnectingEarthquakeSource : IStreamingEarthquakeSource
{
    private readonly IStreamingEarthquakeSource _innerSource;
    private readonly StreamingReconnectPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _streamGate = new(1, 1);

    public ReconnectingEarthquakeSource(
        IStreamingEarthquakeSource innerSource,
        StreamingReconnectPolicy? policy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
        _policy = policy ?? new StreamingReconnectPolicy();
        _delay = delay ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string SourceId => _innerSource.SourceId;

    public async IAsyncEnumerable<EarthquakeSourceFetchResult> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int consecutiveFailures = 0;
            while (true)
            {
                bool receivedOnline = false;
                IAsyncEnumerator<EarthquakeSourceFetchResult>? enumerator = null;
                Exception? sessionException = null;

                try
                {
                    enumerator = _innerSource
                        .StreamAsync(cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsConnectionException(exception))
                {
                    sessionException = exception;
                }

                if (enumerator is not null)
                {
                    try
                    {
                        while (true)
                        {
                            bool hasNext;
                            try
                            {
                                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception) when (IsConnectionException(exception))
                            {
                                sessionException = exception;
                                break;
                            }

                            if (!hasNext)
                            {
                                break;
                            }

                            EarthquakeSourceFetchResult result = enumerator.Current;
                            receivedOnline |= result.Status.State == SourceConnectionState.Online;
                            yield return result;
                            if (result.Status.State == SourceConnectionState.Disconnected)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (sessionException is not null)
                {
                    yield return Failure(
                        SourceConnectionState.Disconnected,
                        $"{SourceId} 流连接异常：{sessionException.Message}");
                }

                cancellationToken.ThrowIfCancellationRequested();
                consecutiveFailures = receivedOnline ? 0 : consecutiveFailures;
                consecutiveFailures++;
                TimeSpan reconnectDelay = _policy.GetDelay(consecutiveFailures);
                DateTimeOffset checkedAt = _utcNow();
                yield return new EarthquakeSourceFetchResult(
                    ImmutableArray<EarthquakeReport>.Empty,
                    new SourceStatus(
                        SourceId,
                        SourceConnectionState.Delayed,
                        checkedAt,
                        Detail: $"第 {consecutiveFailures} 次重连等待",
                        RetryAttempt: consecutiveFailures,
                        NextRetryAt: checkedAt.Add(reconnectDelay)));
                await _delay(reconnectDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private EarthquakeSourceFetchResult Failure(
        SourceConnectionState state,
        string detail)
    {
        return new EarthquakeSourceFetchResult(
            ImmutableArray<EarthquakeReport>.Empty,
            new SourceStatus(SourceId, state, DateTimeOffset.UtcNow, Detail: detail));
    }

    private static bool IsConnectionException(Exception exception) =>
        exception is WebSocketException or IOException;
}
