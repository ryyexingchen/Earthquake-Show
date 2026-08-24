using System.Collections.Immutable;

namespace EarthquakeShow.Core.Models;

public enum EarthquakeReportType
{
    Unknown,
    SeismicIntensity,
    Hypocenter,
    HypocenterAndIntensity,
}

public enum ReportStatus
{
    Unknown,
    Issued,
    Correction,
    Cancelled,
}

public enum ReportContext
{
    Unknown,
    Normal,
    Training,
    Test,
}

public enum TsunamiLevel
{
    Unknown,
    NoConcern,
    MinorChange,
    Advisory,
    Warning,
    MajorWarning,
    Investigating,
}

public sealed record EarthquakeReport
{
    public required string EventId { get; init; }

    public required string ReportCode { get; init; }

    public EarthquakeReportType ReportType { get; init; }

    public ReportStatus Status { get; init; }

    public ReportContext Context { get; init; }

    public int? Serial { get; init; }

    public DateTimeOffset? OriginTime { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public Hypocenter? Hypocenter { get; init; }

    public Magnitude? Magnitude { get; init; }

    public JmaIntensity MaxIntensity { get; init; } = JmaIntensity.Unknown;

    public ImmutableArray<IntensityArea> IntensityAreas { get; init; } = [];

    public ImmutableArray<IntensityMunicipality> IntensityMunicipalities { get; init; } = [];

    public ImmutableArray<IntensityStation> IntensityStations { get; init; } = [];

    public string? TsunamiComment { get; init; }

    public string? TsunamiCommentCode { get; init; }

    public required SourceReference Source { get; init; }
}
