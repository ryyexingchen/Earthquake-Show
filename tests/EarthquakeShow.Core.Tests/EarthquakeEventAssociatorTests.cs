using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class EarthquakeEventAssociatorTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Associate_CloseJmaAndP2pReports_ReturnsHighConfidenceCandidate()
    {
        EarthquakeEvent jma = CreateEvent(
            "jma-event",
            "jma-xml",
            "jma-message",
            BaseTime,
            new GeoCoordinate(32.4, 130.6),
            3.8);
        EarthquakeEvent p2p = CreateEvent(
            "p2pquake:p2p-message",
            "p2pquake",
            "p2p-message",
            BaseTime.AddSeconds(5),
            new GeoCoordinate(32.5, 130.7),
            3.9);

        EarthquakeEventAssociation association = Assert.Single(
            EarthquakeEventAssociator.Associate([jma, p2p]));

        Assert.Equal(EarthquakeAssociationConfidence.High, association.Confidence);
        Assert.Equal(5, association.TimeDifferenceSeconds);
        Assert.InRange(association.DistanceKm, 0, 20);
        Assert.NotNull(association.MagnitudeDifference);
        Assert.InRange(association.MagnitudeDifference.Value, 0.099, 0.101);
    }

    [Theory]
    [InlineData(61, 0, 3.8, 3.8)]
    [InlineData(0, 1.0, 3.8, 3.8)]
    [InlineData(0, 0, 3.8, 4.4)]
    public void Associate_OutsideThreshold_DoesNotAssociate(
        int timeSeconds,
        double longitudeDelta,
        double leftMagnitude,
        double rightMagnitude)
    {
        EarthquakeEvent left = CreateEvent(
            "left",
            "jma-xml",
            "left-message",
            BaseTime,
            new GeoCoordinate(32.4, 130.6),
            leftMagnitude);
        EarthquakeEvent right = CreateEvent(
            "right",
            "p2pquake",
            "right-message",
            BaseTime.AddSeconds(timeSeconds),
            new GeoCoordinate(32.4, 130.6 + longitudeDelta),
            rightMagnitude);

        Assert.Empty(EarthquakeEventAssociator.Associate([left, right]));
    }

    [Fact]
    public void Associate_MissingOriginOrCoordinate_DoesNotAssociate()
    {
        EarthquakeEvent left = CreateEvent(
            "left",
            "jma-xml",
            "left-message",
            BaseTime,
            new GeoCoordinate(32.4, 130.6),
            3.8);
        EarthquakeEvent right = CreateEvent(
            "right",
            "p2pquake",
            "right-message",
            null,
            null,
            3.8);

        Assert.Empty(EarthquakeEventAssociator.Associate([left, right]));
    }

    [Fact]
    public void Associate_SameSourceOrSameEvent_DoesNotAssociate()
    {
        EarthquakeEvent first = CreateEvent(
            "same-event",
            "jma-xml",
            "first",
            BaseTime,
            new GeoCoordinate(32.4, 130.6),
            3.8);
        EarthquakeEvent second = CreateEvent(
            "same-event",
            "p2pquake",
            "second",
            BaseTime,
            new GeoCoordinate(32.4, 130.6),
            3.8);
        EarthquakeEvent sameSource = CreateEvent(
            "other-event",
            "jma-xml",
            "third",
            BaseTime,
            new GeoCoordinate(32.4, 130.6),
            3.8);

        Assert.Empty(EarthquakeEventAssociator.Associate([first, second]));
        Assert.Empty(EarthquakeEventAssociator.Associate([first, sameSource]));
    }

    private static EarthquakeEvent CreateEvent(
        string eventId,
        string sourceId,
        string sourceMessageId,
        DateTimeOffset? originTime,
        GeoCoordinate? coordinate,
        double? magnitude)
    {
        return new EarthquakeEvent
        {
            EventId = eventId,
            Reports =
            [
                new EarthquakeReport
                {
                    EventId = eventId,
                    ReportCode = "TEST",
                    Status = ReportStatus.Issued,
                    Context = ReportContext.Normal,
                    OriginTime = originTime,
                    IssuedAt = BaseTime,
                    ReceivedAt = BaseTime.AddSeconds(1),
                    Hypocenter = coordinate is GeoCoordinate value
                        ? new Hypocenter("测试震源", null, value, 10)
                        : null,
                    Magnitude = magnitude is double valueMagnitude
                        ? new Magnitude(valueMagnitude)
                        : null,
                    Source = new SourceReference(sourceId, sourceMessageId),
                },
            ],
        };
    }
}
