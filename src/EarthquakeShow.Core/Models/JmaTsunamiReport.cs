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

    public ReportContext Context { get; init; }

    public int? Serial { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? OriginTime { get; init; }

    public Hypocenter? Hypocenter { get; init; }

    public Magnitude? Magnitude { get; init; }

    public string? HeadlineText { get; init; }

    public ImmutableArray<JmaTsunamiInformationItem> Items { get; init; } = [];

    public ImmutableArray<JmaTsunamiForecastArea> ForecastAreas { get; init; } = [];

    public ImmutableArray<JmaTsunamiObservationStation> ObservationStations { get; init; } = [];

    public ImmutableArray<JmaTsunamiEstimationArea> EstimationAreas { get; init; } = [];

    public required SourceReference Source { get; init; }
}

public sealed record JmaTsunamiInformationItem(
    string? KindName,
    string? KindCode,
    string? LastKindName,
    string? LastKindCode,
    ImmutableArray<JmaTsunamiArea> Areas);

public sealed record JmaTsunamiArea(string Name, string Code);

public sealed record JmaTsunamiHeight(
    double? Meters,
    string? Description,
    string? Condition,
    string? Unit,
    string? Type);

public sealed record JmaTsunamiForecastArea(
    string Name,
    string Code,
    string? KindName,
    string? KindCode,
    string? LastKindName,
    string? LastKindCode,
    DateTimeOffset? FirstArrivalTime,
    string? FirstArrivalCondition,
    JmaTsunamiHeight? MaximumHeight,
    ImmutableArray<JmaTsunamiStationForecast> Stations);

public sealed record JmaTsunamiStationForecast(
    string Name,
    string Code,
    DateTimeOffset? HighTideTime,
    DateTimeOffset? FirstArrivalTime,
    string? FirstArrivalCondition);

public sealed record JmaTsunamiObservationStation(
    string AreaName,
    string AreaCode,
    string Name,
    string Code,
    string? Sensor,
    DateTimeOffset? FirstArrivalTime,
    string? FirstArrivalCondition,
    string? Initial,
    DateTimeOffset? MaximumHeightTime,
    string? MaximumHeightCondition,
    JmaTsunamiHeight? MaximumHeight);

public sealed record JmaTsunamiEstimationArea(
    string Name,
    string Code,
    DateTimeOffset? FirstArrivalTime,
    string? FirstArrivalCondition,
    JmaTsunamiHeight? MaximumHeight);
