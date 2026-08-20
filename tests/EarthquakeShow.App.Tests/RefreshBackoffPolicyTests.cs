using EarthquakeShow.App.Services;
using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.App.Tests;

public sealed class RefreshBackoffPolicyTests
{
    private static readonly DateTimeOffset CheckedAt =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void GetNextDelay_OnlineStatus_UsesBaseIntervalAndResetsFailures()
    {
        var policy = CreatePolicy();
        policy.GetNextDelay([DisconnectedStatus()]);

        TimeSpan delay = policy.GetNextDelay([OnlineStatus()]);

        Assert.Equal(TimeSpan.FromSeconds(10), delay);
        Assert.Equal(0, policy.ConsecutiveFailureCount);
    }

    [Fact]
    public void GetNextDelay_FailureStatus_UsesExponentialBackoff()
    {
        var policy = CreatePolicy();

        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetNextDelay([DisconnectedStatus()]));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetNextDelay([DisconnectedStatus()]));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.GetNextDelay([DisconnectedStatus()]));
    }

    [Fact]
    public void GetNextDelay_RateLimitedStatus_UsesRateLimitBaseAndMaximum()
    {
        var policy = CreatePolicy();

        Assert.Equal(TimeSpan.FromSeconds(8), policy.GetNextDelay([RateLimitedStatus()]));
        Assert.Equal(TimeSpan.FromSeconds(16), policy.GetNextDelay([RateLimitedStatus()]));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetNextDelay([RateLimitedStatus()]));
    }

    [Fact]
    public void GetNextDelay_MixedStatuses_PrioritizesRateLimit()
    {
        var policy = CreatePolicy();

        TimeSpan delay = policy.GetNextDelay([DisconnectedStatus(), RateLimitedStatus()]);

        Assert.Equal(TimeSpan.FromSeconds(8), delay);
    }

    private static RefreshBackoffPolicy CreatePolicy()
    {
        return new RefreshBackoffPolicy(
            onlineInterval: TimeSpan.FromSeconds(10),
            failureInterval: TimeSpan.FromSeconds(5),
            rateLimitedInterval: TimeSpan.FromSeconds(8),
            maximumInterval: TimeSpan.FromSeconds(30));
    }

    private static SourceStatus OnlineStatus() => new(
        "jma-xml",
        SourceConnectionState.Online,
        CheckedAt);

    private static SourceStatus DisconnectedStatus() => new(
        "jma-xml",
        SourceConnectionState.Disconnected,
        CheckedAt);

    private static SourceStatus RateLimitedStatus() => new(
        "jma-xml",
        SourceConnectionState.RateLimited,
        CheckedAt);
}
