namespace EarthquakeShow.Infrastructure.Sources;

public sealed class StreamingReconnectPolicy
{
    public StreamingReconnectPolicy(
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null)
    {
        InitialDelay = initialDelay ?? TimeSpan.FromSeconds(5);
        MaximumDelay = maximumDelay ?? TimeSpan.FromMinutes(5);
        if (InitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "初始重连间隔必须大于零。");
        }

        if (MaximumDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay), "最大重连间隔不能小于初始间隔。");
        }
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

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
