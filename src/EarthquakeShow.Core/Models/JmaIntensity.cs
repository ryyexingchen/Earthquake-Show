namespace EarthquakeShow.Core.Models;

public enum JmaIntensity
{
    Unknown = 0,
    One = 1,
    Two = 2,
    Three = 3,
    Four = 4,
    FiveLower = 5,
    FiveUpper = 6,
    SixLower = 7,
    SixUpper = 8,
    Seven = 9,
}

public static class JmaIntensityExtensions
{
    public static string ToCode(this JmaIntensity intensity)
    {
        return intensity switch
        {
            JmaIntensity.Unknown => "unknown",
            JmaIntensity.One => "1",
            JmaIntensity.Two => "2",
            JmaIntensity.Three => "3",
            JmaIntensity.Four => "4",
            JmaIntensity.FiveLower => "5-lower",
            JmaIntensity.FiveUpper => "5-upper",
            JmaIntensity.SixLower => "6-lower",
            JmaIntensity.SixUpper => "6-upper",
            JmaIntensity.Seven => "7",
            _ => throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "未知的震度枚举值。"),
        };
    }

    public static bool TryParseCode(string? code, out JmaIntensity intensity)
    {
        intensity = code switch
        {
            "unknown" => JmaIntensity.Unknown,
            "1" => JmaIntensity.One,
            "2" => JmaIntensity.Two,
            "3" => JmaIntensity.Three,
            "4" => JmaIntensity.Four,
            "5-lower" => JmaIntensity.FiveLower,
            "5-upper" => JmaIntensity.FiveUpper,
            "6-lower" => JmaIntensity.SixLower,
            "6-upper" => JmaIntensity.SixUpper,
            "7" => JmaIntensity.Seven,
            _ => JmaIntensity.Unknown,
        };

        return code is "unknown" or "1" or "2" or "3" or "4" or
            "5-lower" or "5-upper" or "6-lower" or "6-upper" or "7";
    }
}
