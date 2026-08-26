using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.Services;

public sealed class RefreshBackoffPolicy
{
    public static readonly TimeSpan DefaultOnlineInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultFailureInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultRateLimitedInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultMaximumInterval = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _onlineInterval;
    private readonly TimeSpan _failureInterval;
    private readonly TimeSpan _rateLimitedInterval;
    private readonly TimeSpan _maximumInterval;
    private int _consecutiveFailureCount;

    public RefreshBackoffPolicy(
        TimeSpan? onlineInterval = null,
        TimeSpan? failureInterval = null,
        TimeSpan? rateLimitedInterval = null,
        TimeSpan? maximumInterval = null)
    {
        _onlineInterval = ValidatePositive(onlineInterval ?? DefaultOnlineInterval, nameof(onlineInterval));
        _failureInterval = ValidatePositive(failureInterval ?? DefaultFailureInterval, nameof(failureInterval));
        _rateLimitedInterval = ValidatePositive(rateLimitedInterval ?? DefaultRateLimitedInterval, nameof(rateLimitedInterval));
        _maximumInterval = ValidatePositive(maximumInterval ?? DefaultMaximumInterval, nameof(maximumInterval));
        if (_maximumInterval < _onlineInterval ||
            _maximumInterval < _failureInterval ||
            _maximumInterval < _rateLimitedInterval)
        {
            throw new ArgumentException("自动刷新最大间隔必须不小于其他基础间隔。", nameof(maximumInterval));
        }
    }

    public int ConsecutiveFailureCount => _consecutiveFailureCount;

    public TimeSpan GetNextDelay(IEnumerable<SourceStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        SourceStatus[] materialized = statuses.ToArray();
        if (materialized.Length > 0 && materialized.All(status =>
                status.State is SourceConnectionState.Online or SourceConnectionState.Delayed))
        {
            _consecutiveFailureCount = 0;
            return _onlineInterval;
        }

        _consecutiveFailureCount = Math.Min(_consecutiveFailureCount + 1, 8);
        TimeSpan baseInterval = materialized.Any(status =>
            status.State == SourceConnectionState.RateLimited)
            ? _rateLimitedInterval
            : _failureInterval;
        double multiplier = Math.Pow(2, _consecutiveFailureCount - 1);
        double milliseconds = Math.Min(
            _maximumInterval.TotalMilliseconds,
            baseInterval.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "自动刷新间隔必须大于零。");
        }

        return value;
    }
}
