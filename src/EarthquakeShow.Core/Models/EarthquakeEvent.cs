using System.Collections.Immutable;

namespace EarthquakeShow.Core.Models;

public sealed record EarthquakeEvent
{
    public required string EventId { get; init; }

    public ImmutableArray<EarthquakeReport> Reports { get; init; } = [];

    public EarthquakeReport? LatestReport =>
        Reports.IsDefaultOrEmpty ? null : Reports[^1];

    public EarthquakeReport? LatestEffectiveReport =>
        Reports.IsDefaultOrEmpty ? null : Reports.LastOrDefault(IsEffectiveReport);

    public EarthquakeEventSummary? Summary
    {
        get
        {
            EarthquakeReport? latestReport = LatestReport;
            if (latestReport is null)
            {
                return null;
            }

            EarthquakeReport? latestEffectiveReport = LatestEffectiveReport;
            EarthquakeReport? latestHypocenterReport = Reports
                .Where(IsEffectiveReport)
                .LastOrDefault(report => report.Hypocenter is not null);
            EarthquakeReport? latestMagnitudeReport = Reports
                .Where(IsEffectiveReport)
                .LastOrDefault(report => report.Magnitude is not null);
            EarthquakeReport? latestIntensityReport = Reports
                .Where(IsEffectiveReport)
                .LastOrDefault(report => report.MaxIntensity != JmaIntensity.Unknown);
            return new EarthquakeEventSummary(
                EventId,
                latestReport.Status,
                latestReport.Context,
                latestReport.IssuedAt,
                latestEffectiveReport?.ReportCode,
                latestHypocenterReport?.OriginTime ?? latestEffectiveReport?.OriginTime,
                latestHypocenterReport?.Hypocenter ?? latestEffectiveReport?.Hypocenter,
                latestMagnitudeReport?.Magnitude ?? latestEffectiveReport?.Magnitude,
                latestIntensityReport?.MaxIntensity ?? JmaIntensity.Unknown);
        }
    }

    private static bool IsEffectiveReport(EarthquakeReport report)
    {
        return report.Status is ReportStatus.Issued or ReportStatus.Correction;
    }
}

public sealed record EarthquakeEventSummary(
    string EventId,
    ReportStatus Status,
    ReportContext Context,
    DateTimeOffset UpdatedAt,
    string? ReportCode,
    DateTimeOffset? OriginTime,
    Hypocenter? Hypocenter,
    Magnitude? Magnitude,
    JmaIntensity MaxIntensity);
