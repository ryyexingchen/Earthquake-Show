namespace EarthquakeShow.Infrastructure.Sources;

public sealed class StreamingReconnectPolicy
{
    public StreamingReconnectPolicy(
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        TimeSpan? maxConnectionDuration = null)
    {
        InitialDelay = initialDelay ?? TimeSpan.FromSeconds(5);
        MaximumDelay = maximumDelay ?? TimeSpan.FromMinutes(5);
        MaxConnectionDuration = maxConnectionDuration ?? TimeSpan.FromMinutes(9);
        if (InitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "初始重连间隔必须大于零。");
        }

        if (MaximumDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay), "最大重连间隔不能小于初始间隔。");
        }

        if (MaxConnectionDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConnectionDuration),
                "单次连接最大持续时间必须大于零。");
        }
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public TimeSpan MaxConnectionDuration { get; }

    public TimeSpan GetDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
        {
            return InitialDelay;
        }

        int exponent = Math.Min(consecutiveFailures - 1, 30);
        double ticks = InitialDelay.Ticks * Math.Pow(2, exponent);
        return ticks >= MaximumDelay.Ticks
            ? MaximumDelay
            : TimeSpan.FromTicks((long)ticks);
    }
}
