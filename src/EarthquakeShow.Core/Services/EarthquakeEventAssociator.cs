using System.Collections.Immutable;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public static class EarthquakeEventAssociator
{
    private const double MaxTimeDifferenceSeconds = 60;
    private const double MaxDistanceKm = 80;
    private const double MaxMagnitudeDifference = 0.5;
    private const double HighConfidenceTimeSeconds = 10;
    private const double HighConfidenceDistanceKm = 30;
    private const double HighConfidenceMagnitudeDifference = 0.2;

    public static ImmutableArray<EarthquakeEventAssociation> Associate(
        IEnumerable<EarthquakeEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        EarthquakeEvent[] materialized = events.ToArray();
        var associations = ImmutableArray.CreateBuilder<EarthquakeEventAssociation>();

        for (int leftIndex = 0; leftIndex < materialized.Length; leftIndex++)
        {
            EarthquakeReport? leftReport = GetAssociationReport(materialized[leftIndex]);
            if (leftReport is null)
            {
                continue;
            }

            for (int rightIndex = leftIndex + 1; rightIndex < materialized.Length; rightIndex++)
            {
                EarthquakeReport? rightReport = GetAssociationReport(materialized[rightIndex]);
                if (rightReport is null ||
                    string.Equals(materialized[leftIndex].EventId, materialized[rightIndex].EventId, StringComparison.Ordinal) ||
                    string.Equals(leftReport.Source.SourceId, rightReport.Source.SourceId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryCreateAssociation(materialized[leftIndex], leftReport, materialized[rightIndex], rightReport)
                    is EarthquakeEventAssociation association)
                {
                    associations.Add(association);
                }
            }
        }

        return associations
            .OrderBy(item => item.LeftEventId, StringComparer.Ordinal)
            .ThenBy(item => item.RightEventId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static EarthquakeReport? GetAssociationReport(EarthquakeEvent earthquakeEvent)
    {
        return earthquakeEvent.Reports
            .Where(report => report.Status is ReportStatus.Issued or ReportStatus.Correction)
            .OrderByDescending(report => report.IssuedAt)
            .ThenByDescending(report => report.ReceivedAt)
            .FirstOrDefault();
    }

    private static EarthquakeEventAssociation? TryCreateAssociation(
        EarthquakeEvent leftEvent,
        EarthquakeReport left,
        EarthquakeEvent rightEvent,
        EarthquakeReport right)
    {
        if (left.OriginTime is not DateTimeOffset leftOrigin ||
            right.OriginTime is not DateTimeOffset rightOrigin ||
            left.Hypocenter?.Coordinate is not GeoCoordinate leftCoordinate ||
            right.Hypocenter?.Coordinate is not GeoCoordinate rightCoordinate)
        {
            return null;
        }

        double timeDifferenceSeconds = Math.Abs((leftOrigin - rightOrigin).TotalSeconds);
        if (timeDifferenceSeconds > MaxTimeDifferenceSeconds)
        {
            return null;
        }

        double distanceKm = CalculateDistanceKm(leftCoordinate, rightCoordinate);
        if (distanceKm > MaxDistanceKm)
        {
            return null;
        }

        double? magnitudeDifference = left.Magnitude?.Value is double leftMagnitude &&
            right.Magnitude?.Value is double rightMagnitude
            ? Math.Abs(leftMagnitude - rightMagnitude)
            : null;
        if (magnitudeDifference > MaxMagnitudeDifference)
        {
            return null;
        }

        EarthquakeAssociationConfidence confidence =
            timeDifferenceSeconds <= HighConfidenceTimeSeconds &&
            distanceKm <= HighConfidenceDistanceKm &&
            (magnitudeDifference is null || magnitudeDifference <= HighConfidenceMagnitudeDifference)
                ? EarthquakeAssociationConfidence.High
                : EarthquakeAssociationConfidence.Medium;
        return new EarthquakeEventAssociation(
            leftEvent.EventId,
            left.Source.SourceId,
            left.Source.SourceMessageId,
            rightEvent.EventId,
            right.Source.SourceId,
            right.Source.SourceMessageId,
            timeDifferenceSeconds,
            distanceKm,
            magnitudeDifference,
            confidence);
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
}
