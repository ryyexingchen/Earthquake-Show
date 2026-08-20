using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using EarthquakeShow.Core.Models;
using EarthquakeShow.Infrastructure.Sources;
using Xunit;

namespace EarthquakeShow.Infrastructure.Tests;

public sealed class ReconnectingEarthquakeSourceTests
{
    [Fact]
    public void Policy_UsesExponentialDelay_AndCapsAtMaximum()
    {
        var policy = new StreamingReconnectPolicy(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(12), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(12), policy.GetDelay(20));
    }

    [Fact]
    public async Task Source_ReconnectsAfterDisconnected_AndCreatesOnlyOneSessionAtATime()
    {
        var inner = new ScriptedSource(
            Result(SourceConnectionState.Disconnected, "first connection closed"),
            Result(SourceConnectionState.Online, "second connection online"));
        var delays = new List<TimeSpan>();
        var source = new ReconnectingEarthquakeSource(
            inner,
            new StreamingReconnectPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5)),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(SourceConnectionState.Disconnected, enumerator.Current.Status.State);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(SourceConnectionState.Online, enumerator.Current.Status.State);
        Assert.Equal(2, inner.SessionCount);
        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task Source_ResetsFailureCountAfterOnline()
    {
        var inner = new ScriptedSource(
            Result(SourceConnectionState.Online, "online"),
            Result(SourceConnectionState.Disconnected, "closed"),
            Result(SourceConnectionState.Disconnected, "closed again"));
        var delays = new List<TimeSpan>();
        var source = new ReconnectingEarthquakeSource(
            inner,
            new StreamingReconnectPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5)),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            delays);
    }

    [Fact]
    public async Task Source_CancellationInterruptsReconnectDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new ScriptedSource(
            Result(SourceConnectionState.Disconnected, "closed"));
        var source = new ReconnectingEarthquakeSource(
            inner,
            new StreamingReconnectPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        await using IAsyncEnumerator<EarthquakeSourceFetchResult> enumerator =
            source.StreamAsync(cancellation.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
    }

    [Fact]
    public async Task Source_AllowsOnlyOneActiveConsumer()
    {
        var inner = new ScriptedSource(
            Result(SourceConnectionState.Online, "first"),
            Result(SourceConnectionState.Online, "second"));
        var source = new ReconnectingEarthquakeSource(
            inner,
            delay: (_, _) => Task.CompletedTask);
        IAsyncEnumerator<EarthquakeSourceFetchResult> first =
            source.StreamAsync().GetAsyncEnumerator();
        IAsyncEnumerator<EarthquakeSourceFetchResult> second =
            source.StreamAsync().GetAsyncEnumerator();

        try
        {
            Assert.True(await first.MoveNextAsync());
            Task<bool> secondMove = second.MoveNextAsync().AsTask();
            await Task.Yield();
            Assert.False(secondMove.IsCompleted);

            await first.DisposeAsync();
            Assert.True(await secondMove);
            Assert.Equal(2, inner.SessionCount);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private static EarthquakeSourceFetchResult Result(
        SourceConnectionState state,
        string detail) =>
        new(
            ImmutableArray<EarthquakeReport>.Empty,
            new SourceStatus("test", state, DateTimeOffset.UtcNow, Detail: detail));

    private sealed class ScriptedSource(params EarthquakeSourceFetchResult[] sessions)
        : IStreamingEarthquakeSource
    {
        private readonly Queue<EarthquakeSourceFetchResult> _sessions = new(sessions);

        public string SourceId => "test";

        public int SessionCount { get; private set; }

        public async IAsyncEnumerable<EarthquakeSourceFetchResult> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SessionCount++;
            if (_sessions.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return _sessions.Dequeue();
        }
    }
}
