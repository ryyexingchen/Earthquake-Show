namespace EarthquakeShow.Core.Models;

public readonly record struct GeoCoordinate
{
    public GeoCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "纬度必须是 -90 到 90 之间的有限值。");
        }

        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "经度必须是 -180 到 180 之间的有限值。");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }
}

public sealed record Magnitude
{
    public Magnitude(
        double? value,
        string? type = null,
        string? condition = null,
        string? description = null)
    {
        if (value is double numericValue && !double.IsFinite(numericValue))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "震级必须是有限值，未知震级应使用 null。");
        }

        Value = value;
        Type = type;
        Condition = condition;
        Description = description;
    }

    public double? Value { get; }

    public string? Type { get; }

    public string? Condition { get; }

    public string? Description { get; }
}

public sealed record Hypocenter
{
    public Hypocenter(
        string? name,
        string? code,
        GeoCoordinate? coordinate,
        int? depthKm,
        string? description = null)
    {
        if (depthKm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depthKm), depthKm, "震源深度不能为负数。");
        }

        Name = name;
        Code = code;
        Coordinate = coordinate;
        DepthKm = depthKm;
        Description = description;
    }

    public string? Name { get; }

    public string? Code { get; }

    public GeoCoordinate? Coordinate { get; }

    public int? DepthKm { get; }

    public string? Description { get; }
}
