namespace EarthquakeShow.Core.Models;

public enum EarthquakeAssociationConfidence
{
    Medium,
    High,
}

public sealed record EarthquakeEventAssociation(
    string LeftEventId,
    string LeftSourceId,
    string LeftSourceMessageId,
    string RightEventId,
    string RightSourceId,
    string RightSourceMessageId,
    double TimeDifferenceSeconds,
    double DistanceKm,
    double? MagnitudeDifference,
    EarthquakeAssociationConfidence Confidence);
