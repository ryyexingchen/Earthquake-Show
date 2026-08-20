using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public static class EarthquakeEventMerger
{
    public static ImmutableArray<EarthquakeEvent> Merge(IEnumerable<EarthquakeReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        EarthquakeReport[] materializedReports = reports.ToArray();
        foreach (EarthquakeReport report in materializedReports)
        {
            ValidateReportIdentity(report);
        }

        return materializedReports
            .GroupBy(report => report.EventId, StringComparer.Ordinal)
            .Select(group => new EarthquakeEvent
            {
                EventId = group.Key,
                Reports = BuildTimeline(group),
            })
            .OrderByDescending(earthquakeEvent => earthquakeEvent.LatestReport!.IssuedAt)
            .ThenBy(earthquakeEvent => earthquakeEvent.EventId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<EarthquakeReport> BuildTimeline(
        IEnumerable<EarthquakeReport> reports)
    {
        return reports
            .GroupBy(
                report => (report.Source.SourceId, report.Source.SourceMessageId))
            .Select(group => group
                .OrderBy(report => report.ReceivedAt)
                .ThenBy(report => report.IssuedAt)
                .ThenBy(report => report.Serial.HasValue ? 1 : 0)
                .ThenBy(report => report.Serial)
                .ThenBy(report => report.ReportCode, StringComparer.Ordinal)
                .First())
            .OrderBy(report => report.IssuedAt)
            .ThenBy(report => report.Serial.HasValue ? 1 : 0)
            .ThenBy(report => report.Serial)
            .ThenBy(report => report.ReceivedAt)
            .ThenBy(report => GetSourcePriority(report.Source.SourceId))
            .ThenBy(report => report.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(report => report.Source.SourceMessageId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static int GetSourcePriority(string sourceId)
    {
        return sourceId switch
        {
            "p2pquake" => 5,
            "jma-json" => 10,
            "jma-xml" => 20,
            _ => 0,
        };
    }

    private static void ValidateReportIdentity(EarthquakeReport report)
    {
        if (report is null)
        {
            throw new ArgumentException("报文集合不能包含 null。", nameof(report));
        }

        if (string.IsNullOrWhiteSpace(report.EventId))
        {
            throw new ArgumentException("报文 EventId 不能为空。", nameof(report));
        }

        if (report.Source is null)
        {
            throw new ArgumentException("报文来源不能为空。", nameof(report));
        }

        if (string.IsNullOrWhiteSpace(report.Source.SourceId))
        {
            throw new ArgumentException("报文 SourceId 不能为空。", nameof(report));
        }

        if (string.IsNullOrWhiteSpace(report.Source.SourceMessageId))
        {
            throw new ArgumentException("报文 SourceMessageId 不能为空。", nameof(report));
        }
    }
}
