using EarthquakeShow.Core.Models;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaIntensityTests
{
    [Theory]
    [InlineData("unknown", JmaIntensity.Unknown)]
    [InlineData("1", JmaIntensity.One)]
    [InlineData("2", JmaIntensity.Two)]
    [InlineData("3", JmaIntensity.Three)]
    [InlineData("4", JmaIntensity.Four)]
    [InlineData("5-lower", JmaIntensity.FiveLower)]
    [InlineData("5-upper", JmaIntensity.FiveUpper)]
    [InlineData("6-lower", JmaIntensity.SixLower)]
    [InlineData("6-upper", JmaIntensity.SixUpper)]
    [InlineData("7", JmaIntensity.Seven)]
    public void TryParseCode_KnownCode_RoundTrips(string code, JmaIntensity expected)
    {
        bool parsed = JmaIntensityExtensions.TryParseCode(code, out JmaIntensity actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
        Assert.Equal(code, actual.ToCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("5弱")]
    [InlineData("invalid")]
    public void TryParseCode_UnknownCode_ReturnsExplicitUnknown(string? code)
    {
        bool parsed = JmaIntensityExtensions.TryParseCode(code, out JmaIntensity actual);

        Assert.False(parsed);
        Assert.Equal(JmaIntensity.Unknown, actual);
        Assert.NotEqual(JmaIntensity.One, actual);
    }
}
