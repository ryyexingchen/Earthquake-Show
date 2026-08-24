using System.Collections.Immutable;

namespace EarthquakeShow.Core.Models;

/// <summary>
/// 独立 JMAXML 海啸报文的结构化快照，不与地震报文的 ForecastComment 混用。
/// </summary>
public sealed record JmaTsunamiReport
{
    public required string EventId { get; init; }

    public required string ReportCode { get; init; }

    public string? InfoKind { get; init; }

    public ReportStatus Status { get; init; }

    public int? Serial { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public string? HeadlineText { get; init; }

    public ImmutableArray<JmaTsunamiInformationItem> Items { get; init; } = [];

    public required SourceReference Source { get; init; }
}

public sealed record JmaTsunamiInformationItem(
    string? KindName,
    string? KindCode,
    string? LastKindName,
    string? LastKindCode,
    ImmutableArray<JmaTsunamiArea> Areas);

public sealed record JmaTsunamiArea(string Name, string Code);
