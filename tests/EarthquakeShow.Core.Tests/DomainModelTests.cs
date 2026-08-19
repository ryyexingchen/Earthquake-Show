using System.Collections.Immutable;
using System.Globalization;
using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class DomainModelTests
{
    [Fact]
    public void Hypocenter_ZeroDepthAndUnknownDepth_RemainDistinct()
    {
        var surface = new Hypocenter("测试震源", "000", null, 0);
        var unknown = new Hypocenter("测试震源", "000", null, null);

        Assert.Equal(0, surface.DepthKm);
        Assert.Null(unknown.DepthKm);
    }

    [Fact]
    public void Hypocenter_NegativeDepth_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Hypocenter("测试震源", "000", null, -1));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Magnitude_NonFiniteValue_IsRejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Magnitude(value, "Mj"));
    }

    [Fact]
    public void Magnitude_NullValue_RepresentsUnknown()
    {
        var magnitude = new Magnitude(null, "Mj", "Ｍ不明");

        Assert.Null(magnitude.Value);
        Assert.Equal("Ｍ不明", magnitude.Condition);
    }

    [Theory]
    [InlineData(-90, -180)]
    [InlineData(32.6, 130.6)]
    [InlineData(90, 180)]
    public void GeoCoordinate_ValidCoordinate_IsPreserved(double latitude, double longitude)
    {
        var coordinate = new GeoCoordinate(latitude, longitude);

        Assert.Equal(latitude, coordinate.Latitude);
        Assert.Equal(longitude, coordinate.Longitude);
    }

    [Theory]
    [InlineData(-90.1, 0)]
    [InlineData(90.1, 0)]
    [InlineData(0, -180.1)]
    [InlineData(0, 180.1)]
    [InlineData(double.NaN, 0)]
    public void GeoCoordinate_InvalidCoordinate_IsRejected(double latitude, double longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude, longitude));
    }

    [Fact]
    public void EarthquakeReport_DateTimeOffsetAndSourceFields_ArePreserved()
    {
        DateTimeOffset issuedAt = DateTimeOffset.Parse(
            "2026-08-19T07:14:00+09:00",
            CultureInfo.InvariantCulture);
        const string payload = "<Report><EventID>20260819071048</EventID></Report>";
        var source = new SourceReference(
            "jma-xml",
            "20260818221432_0_VXSE53_270000",
            new Uri("https://www.data.jma.go.jp/developer/xml/data/example.xml"),
            payload);

        var report = CreateReport(issuedAt, source);

        Assert.Equal(TimeSpan.FromHours(9), report.IssuedAt.Offset);
        Assert.Equal("2026-08-19T07:14:00.0000000+09:00", report.IssuedAt.ToString("O"));
        Assert.Equal("VXSE53", report.ReportCode);
        Assert.Equal("20260818221432_0_VXSE53_270000", report.Source.SourceMessageId);
        Assert.Equal(payload, report.Source.SourcePayload);
        Assert.Equal("https://www.data.jma.go.jp/developer/xml/data/example.xml", report.Source.RawMessageUri?.AbsoluteUri);
    }

    [Fact]
    public void EarthquakeEvent_StoresImmutableReportTimeline()
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse(
            "2026-08-19T07:14:00+09:00",
            CultureInfo.InvariantCulture);
        EarthquakeReport report = CreateReport(
            timestamp,
            new SourceReference("jma-xml", "message-1"));
        var earthquakeEvent = new EarthquakeEvent
        {
            EventId = report.EventId,
            Reports = ImmutableArray.Create(report),
        };

        Assert.Equal("20260819071048", earthquakeEvent.EventId);
        Assert.Single(earthquakeEvent.Reports);
        Assert.Equal(report, earthquakeEvent.Reports[0]);
    }

    [Fact]
    public void EarthquakeEvent_DefaultTimeline_HasNoDerivedReportOrSummary()
    {
        var earthquakeEvent = new EarthquakeEvent
        {
            EventId = "20260819071048",
            Reports = default,
        };

        Assert.Null(earthquakeEvent.LatestReport);
        Assert.Null(earthquakeEvent.LatestEffectiveReport);
        Assert.Null(earthquakeEvent.Summary);
    }

    private static EarthquakeReport CreateReport(
        DateTimeOffset timestamp,
        SourceReference source)
    {
        return new EarthquakeReport
        {
            EventId = "20260819071048",
            ReportCode = "VXSE53",
            ReportType = EarthquakeReportType.HypocenterAndIntensity,
            Status = ReportStatus.Issued,
            Context = ReportContext.Normal,
            Serial = 1,
            OriginTime = timestamp.AddMinutes(-4),
            IssuedAt = timestamp,
            ReceivedAt = timestamp.AddSeconds(1),
            Hypocenter = new Hypocenter(
                "熊本県天草・芦北地方",
                "743",
                new GeoCoordinate(32.6, 130.6),
                10),
            Magnitude = new Magnitude(3.8, "Mj"),
            MaxIntensity = JmaIntensity.Three,
            Source = source,
        };
    }
}
