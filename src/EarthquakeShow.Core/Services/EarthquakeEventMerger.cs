using System.Collections.Immutable;
using System.Globalization;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public static class EarthquakeEventMerger
{
    private const double TemporaryEventIdDifferenceSeconds = 60;
    private const double TemporaryReportTimeDifferenceSeconds = 180;
    private const double CrossSourceTimeDifferenceSeconds = 60;
    private const double CrossSourceDistanceKm = 80;
    private const double CrossSourceMagnitudeDifference = 0.5;

    public static ImmutableArray<EarthquakeEvent> Merge(IEnumerable<EarthquakeReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        EarthquakeReport[] materializedReports = reports.ToArray();
        foreach (EarthquakeReport report in materializedReports)
        {
            ValidateReportIdentity(report);
        }

        List<List<EarthquakeReport>> groups = materializedReports
            .GroupBy(report => report.EventId, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();
        MergeTemporaryJmaEvents(groups);
        MergeTemporaryP2pEvents(groups);
        MergeCrossSourceEvents(groups);

        return groups
            .Select(group => new EarthquakeEvent
            {
                EventId = GetCanonicalEventId(group),
                Reports = BuildTimeline(group),
            })
            .OrderByDescending(earthquakeEvent => earthquakeEvent.LatestReport!.IssuedAt)
            .ThenBy(earthquakeEvent => earthquakeEvent.EventId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void MergeTemporaryJmaEvents(List<List<EarthquakeReport>> groups)
    {
        bool merged;
        do
        {
            merged = false;
            for (int leftIndex = 0; leftIndex < groups.Count && !merged; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
                {
                    if (!CanMergeTemporaryJmaEvents(groups[leftIndex], groups[rightIndex]))
                    {
                        continue;
                    }

                    groups[leftIndex].AddRange(groups[rightIndex]);
                    groups.RemoveAt(rightIndex);
                    merged = true;
                    break;
                }
            }
        }
        while (merged);
    }

    private static void MergeCrossSourceEvents(List<List<EarthquakeReport>> groups)
    {
        bool merged;
        do
        {
            merged = false;
            for (int leftIndex = 0; leftIndex < groups.Count && !merged; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
                {
                    if (!CanMergeCrossSourceEvents(groups[leftIndex], groups[rightIndex]))
                    {
                        continue;
                    }

                    groups[leftIndex].AddRange(groups[rightIndex]);
                    groups.RemoveAt(rightIndex);
                    merged = true;
                    break;
                }
            }
        }
        while (merged);
    }

    private static void MergeTemporaryP2pEvents(List<List<EarthquakeReport>> groups)
    {
        bool merged;
        do
        {
            merged = false;
            for (int leftIndex = 0; leftIndex < groups.Count && !merged; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
                {
                    if (!CanMergeTemporaryP2pEvents(groups[leftIndex], groups[rightIndex]))
                    {
                        continue;
                    }

                    groups[leftIndex].AddRange(groups[rightIndex]);
                    groups.RemoveAt(rightIndex);
                    merged = true;
                    break;
                }
            }
        }
        while (merged);
    }

    private static bool CanMergeTemporaryP2pEvents(
        IReadOnlyList<EarthquakeReport> left,
        IReadOnlyList<EarthquakeReport> right)
    {
        if (left.Count == 0 || right.Count == 0 ||
            left.Any(report => report.Source.SourceId != "p2pquake") ||
            right.Any(report => report.Source.SourceId != "p2pquake"))
        {
            return false;
        }

        if (CanMergeRepeatedDistantP2pEvents(left, right))
        {
            return true;
        }

        HashSet<EarthquakeReportType> leftStages = left
            .Select(report => report.ReportType)
            .ToHashSet();
        HashSet<EarthquakeReportType> rightStages = right
            .Select(report => report.ReportType)
            .ToHashSet();
        if (leftStages.Contains(EarthquakeReportType.Unknown) ||
            rightStages.Contains(EarthquakeReportType.Unknown) ||
            leftStages.Overlaps(rightStages))
        {
            return false;
        }

        EarthquakeReport? leftOriginReport = GetOriginReport(left);
        EarthquakeReport? rightOriginReport = GetOriginReport(right);
        if (leftOriginReport?.OriginTime is not DateTimeOffset leftOrigin ||
            rightOriginReport?.OriginTime is not DateTimeOffset rightOrigin ||
            Math.Abs((leftOrigin - rightOrigin).TotalSeconds) > TemporaryEventIdDifferenceSeconds)
        {
            return false;
        }

        DateTimeOffset firstIssuedAt = left.Concat(right).Min(report => report.IssuedAt);
        DateTimeOffset lastIssuedAt = left.Concat(right).Max(report => report.IssuedAt);
        if ((lastIssuedAt - firstIssuedAt).TotalSeconds > TemporaryReportTimeDifferenceSeconds)
        {
            return false;
        }

        JmaIntensity leftIntensity = GetKnownMaximumIntensity(left);
        JmaIntensity rightIntensity = GetKnownMaximumIntensity(right);
        if (leftIntensity != JmaIntensity.Unknown &&
            rightIntensity != JmaIntensity.Unknown &&
            leftIntensity != rightIntensity)
        {
            return false;
        }

        EarthquakeReport? leftAssociation = GetAssociationReport(left);
        EarthquakeReport? rightAssociation = GetAssociationReport(right);
        if (leftAssociation is null && rightAssociation is null)
        {
            return false;
        }

        if (leftAssociation?.Hypocenter?.Coordinate is GeoCoordinate leftCoordinate &&
            rightAssociation?.Hypocenter?.Coordinate is GeoCoordinate rightCoordinate)
        {
            if (CalculateDistanceKm(leftCoordinate, rightCoordinate) > CrossSourceDistanceKm)
            {
                return false;
            }

            if (leftAssociation.Magnitude?.Value is double leftMagnitude &&
                rightAssociation.Magnitude?.Value is double rightMagnitude &&
                Math.Abs(leftMagnitude - rightMagnitude) > CrossSourceMagnitudeDifference)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanMergeRepeatedDistantP2pEvents(
        IReadOnlyList<EarthquakeReport> left,
        IReadOnlyList<EarthquakeReport> right)
    {
        if (left.Any(report => report.ReportType != EarthquakeReportType.DistantEarthquake) ||
            right.Any(report => report.ReportType != EarthquakeReportType.DistantEarthquake))
        {
            return false;
        }

        if (left.Any(report => report.DistantEarthquakeKind is null) ||
            right.Any(report => report.DistantEarthquakeKind is null))
        {
            return false;
        }

        DistantEarthquakeKind leftKind = left[0].DistantEarthquakeKind!.Value;
        DistantEarthquakeKind rightKind = right[0].DistantEarthquakeKind!.Value;
        if (left.Any(report => report.DistantEarthquakeKind != leftKind) ||
            right.Any(report => report.DistantEarthquakeKind != rightKind) ||
            leftKind != rightKind)
        {
            return false;
        }

        EarthquakeReport? leftAssociation = GetAssociationReport(left);
        EarthquakeReport? rightAssociation = GetAssociationReport(right);
        if (leftAssociation?.OriginTime is not DateTimeOffset leftOrigin ||
            rightAssociation?.OriginTime is not DateTimeOffset rightOrigin ||
            leftAssociation.Hypocenter?.Coordinate is not GeoCoordinate leftCoordinate ||
            rightAssociation.Hypocenter?.Coordinate is not GeoCoordinate rightCoordinate)
        {
            return false;
        }

        if (Math.Abs((leftOrigin - rightOrigin).TotalSeconds) > TemporaryEventIdDifferenceSeconds ||
            CalculateDistanceKm(leftCoordinate, rightCoordinate) > CrossSourceDistanceKm)
        {
            return false;
        }

        if (leftAssociation.Magnitude?.Value is double leftMagnitude &&
            rightAssociation.Magnitude?.Value is double rightMagnitude &&
            Math.Abs(leftMagnitude - rightMagnitude) > CrossSourceMagnitudeDifference)
        {
            return false;
        }

        return true;
    }

    private static bool CanMergeCrossSourceEvents(
        IReadOnlyList<EarthquakeReport> left,
        IReadOnlyList<EarthquakeReport> right)
    {
        string[] leftSources = left.Select(report => report.Source.SourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] rightSources = right.Select(report => report.Source.SourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (leftSources.Length != 1 || rightSources.Length != 1 ||
            !IsP2pAndJmaXml(leftSources[0], rightSources[0]))
        {
            return false;
        }

        EarthquakeReport? leftReport = GetAssociationReport(left);
        EarthquakeReport? rightReport = GetAssociationReport(right);
        if (leftReport?.OriginTime is not DateTimeOffset leftOrigin ||
            rightReport?.OriginTime is not DateTimeOffset rightOrigin ||
            leftReport.Hypocenter?.Coordinate is not GeoCoordinate leftCoordinate ||
            rightReport.Hypocenter?.Coordinate is not GeoCoordinate rightCoordinate)
        {
            return false;
        }

        if (Math.Abs((leftOrigin - rightOrigin).TotalSeconds) > CrossSourceTimeDifferenceSeconds ||
            CalculateDistanceKm(leftCoordinate, rightCoordinate) > CrossSourceDistanceKm)
        {
            return false;
        }

        if (leftReport.Magnitude?.Value is double leftMagnitude &&
            rightReport.Magnitude?.Value is double rightMagnitude &&
            Math.Abs(leftMagnitude - rightMagnitude) > CrossSourceMagnitudeDifference)
        {
            return false;
        }

        return true;
    }

    private static bool IsP2pAndJmaXml(string left, string right)
    {
        return left is "p2pquake" && right is "jma-xml" ||
            left is "jma-xml" && right is "p2pquake";
    }

    private static EarthquakeReport? GetAssociationReport(
        IEnumerable<EarthquakeReport> reports)
    {
        return reports
            .Where(report => report.Status is ReportStatus.Issued or ReportStatus.Correction)
            .OrderByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.ReceivedAt)
            .FirstOrDefault(report =>
                report.OriginTime is not null &&
                report.Hypocenter?.Coordinate is not null);
    }

    private static EarthquakeReport? GetOriginReport(
        IEnumerable<EarthquakeReport> reports)
    {
        return reports
            .Where(report => report.Status is ReportStatus.Issued or ReportStatus.Correction)
            .OrderByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.ReceivedAt)
            .FirstOrDefault(report => report.OriginTime is not null);
    }

    private static double CalculateDistanceKm(
        GeoCoordinate left,
        GeoCoordinate right)
    {
        const double earthRadiusKm = 6371.0088;
        double latitudeDelta = DegreesToRadians(right.Latitude - left.Latitude);
        double longitudeDelta = DegreesToRadians(right.Longitude - left.Longitude);
        double leftLatitude = DegreesToRadians(left.Latitude);
        double rightLatitude = DegreesToRadians(right.Latitude);
        double haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            Math.Cos(leftLatitude) * Math.Cos(rightLatitude) *
            Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusKm * 2 * Math.Asin(Math.Sqrt(haversine));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static bool CanMergeTemporaryJmaEvents(
        IReadOnlyList<EarthquakeReport> left,
        IReadOnlyList<EarthquakeReport> right)
    {
        if (left.Count == 0 || right.Count == 0 ||
            left.Any(report => report.Source.SourceId != "jma-xml") ||
            right.Any(report => report.Source.SourceId != "jma-xml"))
        {
            return false;
        }

        bool leftHasIntensity速報 = left.Any(report => report.ReportCode == "VXSE51");
        bool rightHasIntensity速報 = right.Any(report => report.ReportCode == "VXSE51");
        bool leftHasHypocenter = left.Any(IsHypocenterReport);
        bool rightHasHypocenter = right.Any(IsHypocenterReport);
        if (leftHasIntensity速報 == rightHasIntensity速報 ||
            leftHasHypocenter == rightHasHypocenter)
        {
            return false;
        }

        if (!TryParseTemporaryEventTime(left[0].EventId, out DateTime leftEventTime) ||
            !TryParseTemporaryEventTime(right[0].EventId, out DateTime rightEventTime) ||
            Math.Abs((leftEventTime - rightEventTime).TotalSeconds) > TemporaryEventIdDifferenceSeconds)
        {
            return false;
        }

        DateTimeOffset leftIssuedAt = left.Max(report => report.IssuedAt);
        DateTimeOffset rightIssuedAt = right.Max(report => report.IssuedAt);
        if (Math.Abs((leftIssuedAt - rightIssuedAt).TotalSeconds) > TemporaryReportTimeDifferenceSeconds)
        {
            return false;
        }

        JmaIntensity leftIntensity = GetKnownMaximumIntensity(left);
        JmaIntensity rightIntensity = GetKnownMaximumIntensity(right);
        return leftIntensity == JmaIntensity.Unknown ||
            rightIntensity == JmaIntensity.Unknown ||
            leftIntensity == rightIntensity;
    }

    private static bool IsHypocenterReport(EarthquakeReport report)
    {
        return report.ReportCode is "VXSE52" or "VXSE53";
    }

    private static JmaIntensity GetKnownMaximumIntensity(
        IEnumerable<EarthquakeReport> reports)
    {
        return reports
            .Select(report => report.MaxIntensity)
            .Where(intensity => intensity != JmaIntensity.Unknown)
            .DefaultIfEmpty(JmaIntensity.Unknown)
            .Max();
    }

    private static bool TryParseTemporaryEventTime(
        string eventId,
        out DateTime eventTime)
    {
        return DateTime.TryParseExact(
            eventId,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out eventTime);
    }

    private static string GetCanonicalEventId(
        IReadOnlyList<EarthquakeReport> reports)
    {
        return reports
            .Where(IsHypocenterReport)
            .OrderByDescending(report => report.Source.SourceId == "jma-xml")
            .ThenByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.EventId, StringComparer.Ordinal)
            .Select(report => report.EventId)
            .FirstOrDefault() ?? reports[0].EventId;
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
