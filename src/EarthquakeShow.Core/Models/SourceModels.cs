namespace EarthquakeShow.Core.Models;

public enum SourceConnectionState
{
    Unknown,
    Online,
    Delayed,
    Disconnected,
    RateLimited,
    ParseFailed,
    Disabled,
}

public sealed record SourceReference(
    string SourceId,
    string SourceMessageId,
    Uri? RawMessageUri = null,
    string? SourcePayload = null);

public sealed record SourceStatus(
    string SourceId,
    SourceConnectionState State,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastReceivedAt = null,
    string? Detail = null,
    int? RetryAttempt = null,
    DateTimeOffset? NextRetryAt = null,
    DateTimeOffset? ConnectedAt = null,
    DateTimeOffset? ConnectionEndedAt = null,
    string? LastError = null,
    DateTimeOffset? LastMessageAt = null,
    int? ConnectionExceptionCount = null,
    DateTimeOffset? LastConnectionExceptionAt = null,
    bool IsExpectedDisconnect = false);
