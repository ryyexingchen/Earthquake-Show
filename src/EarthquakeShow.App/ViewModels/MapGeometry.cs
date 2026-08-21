using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record MapPolygonGeometry(
    string Code,
    string Name,
    ImmutableArray<GeoCoordinate> Coordinates,
    bool IsOfficialBoundary)
{
    /// <summary>当前多边形的外环和内环；Coordinates 保留为外环兼容属性。</summary>
    public ImmutableArray<ImmutableArray<GeoCoordinate>> Rings { get; init; } =
        ImmutableArray.Create(Coordinates);
}

public readonly record struct MapGeometryBounds(
    double MinLongitude,
    double MaxLongitude,
    double MinLatitude,
    double MaxLatitude)
{
    public double LongitudeSpan => Math.Max(0.000001, MaxLongitude - MinLongitude);

    public double LatitudeSpan => Math.Max(0.000001, MaxLatitude - MinLatitude);
}

public sealed class OfflineMapGeometry
{
    private OfflineMapGeometry(
        ImmutableArray<MapPolygonGeometry> polygons,
        string source,
        bool isOfficialBoundary,
        int invalidGeometryCount,
        MapGeometryBounds bounds)
    {
        Polygons = polygons;
        Source = source;
        IsOfficialBoundary = isOfficialBoundary;
        InvalidGeometryCount = invalidGeometryCount;
        Bounds = bounds;
    }

    public ImmutableArray<MapPolygonGeometry> Polygons { get; }

    public string Source { get; }

    public bool IsOfficialBoundary { get; }

    public int InvalidGeometryCount { get; }

    public MapGeometryBounds Bounds { get; }

    public static OfflineMapGeometry LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("features", out JsonElement features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("GeoJSON 必须包含 features 数组。");
        }

        var polygons = ImmutableArray.CreateBuilder<MapPolygonGeometry>();
        int invalidGeometryCount = 0;
        foreach (JsonElement feature in features.EnumerateArray())
        {
            JsonElement properties = feature.GetProperty("properties");
            JsonElement geometry = feature.GetProperty("geometry");
            string code = GetString(properties, "code") ??
                GetString(properties, "areaCode") ?? string.Empty;
            string name = GetString(properties, "name") ?? code;
            bool officialBoundary = properties.TryGetProperty(
                    "officialBoundary",
                    out JsonElement officialElement) &&
                officialElement.ValueKind == JsonValueKind.True;

            string geometryType = geometry.GetProperty("type").GetString() ?? string.Empty;
            JsonElement coordinates = geometry.GetProperty("coordinates");
            switch (geometryType)
            {
                case "Polygon":
                    if (!AddPolygon(polygons, code, name, coordinates, officialBoundary))
                    {
                        invalidGeometryCount++;
                    }
                    break;
                case "MultiPolygon":
                    if (!AddMultiPolygon(polygons, code, name, coordinates, officialBoundary))
                    {
                        invalidGeometryCount++;
                    }
                    break;
                default:
                    throw new FormatException($"不支持的 GeoJSON 几何类型：{geometryType}。");
            }
        }

        if (polygons.Count == 0)
        {
            throw new FormatException("GeoJSON 没有可绘制的多边形。");
        }

        string source = root.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.TryGetProperty("source", out JsonElement sourceElement)
            ? sourceElement.GetString() ?? "未注明来源"
            : "未注明来源";
        bool allOfficial = polygons.All(item => item.IsOfficialBoundary);
        MapGeometryBounds bounds = CalculateBounds(polygons);
        return new OfflineMapGeometry(
            polygons.ToImmutable(),
            source,
            allOfficial,
            invalidGeometryCount,
            bounds);
    }

    public static OfflineMapGeometry LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    private static bool AddPolygon(
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        string code,
        string name,
        JsonElement polygonCoordinates,
        bool officialBoundary)
    {
        if (polygonCoordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var rings = ImmutableArray.CreateBuilder<ImmutableArray<GeoCoordinate>>();
        ReadRings(polygonCoordinates, rings);
        return AddRings(polygons, code, name, rings, officialBoundary);
    }

    private static bool AddMultiPolygon(
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        string code,
        string name,
        JsonElement multiPolygonCoordinates,
        bool officialBoundary)
    {
        if (multiPolygonCoordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var rings = ImmutableArray.CreateBuilder<ImmutableArray<GeoCoordinate>>();
        foreach (JsonElement polygon in multiPolygonCoordinates.EnumerateArray())
        {
            if (polygon.ValueKind == JsonValueKind.Array)
            {
                ReadRings(polygon, rings);
            }
        }

        return AddRings(polygons, code, name, rings, officialBoundary);
    }

    private static void ReadRings(
        JsonElement polygonCoordinates,
        ImmutableArray<ImmutableArray<GeoCoordinate>>.Builder rings)
    {
        foreach (JsonElement ring in polygonCoordinates.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var points = ImmutableArray.CreateBuilder<GeoCoordinate>();
            foreach (JsonElement coordinate in ring.EnumerateArray())
            {
                if (coordinate.ValueKind != JsonValueKind.Array || coordinate.GetArrayLength() < 2)
                {
                    continue;
                }

                double longitude = coordinate[0].GetDouble();
                double latitude = coordinate[1].GetDouble();
                if (!double.IsFinite(longitude) || !double.IsFinite(latitude))
                {
                    continue;
                }

                points.Add(new GeoCoordinate(latitude, longitude));
            }

            if (points.Count >= 3)
            {
                rings.Add(points.ToImmutable());
            }
        }
    }

    private static bool AddRings(
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        string code,
        string name,
        ImmutableArray<ImmutableArray<GeoCoordinate>>.Builder rings,
        bool officialBoundary)
    {
        if (rings.Count == 0)
        {
            return false;
        }

        ImmutableArray<GeoCoordinate> outerRing = rings[0];
        polygons.Add(new MapPolygonGeometry(
                code,
                name,
                outerRing,
                officialBoundary)
        {
                Rings = rings.ToImmutable(),
        });
        return true;
    }

    private static MapGeometryBounds CalculateBounds(
        ImmutableArray<MapPolygonGeometry>.Builder polygons)
    {
        IEnumerable<GeoCoordinate> coordinates = polygons
            .SelectMany(item => item.Rings.IsDefaultOrEmpty
                ? [item.Coordinates]
                : item.Rings)
            .SelectMany(item => item);
        return new MapGeometryBounds(
            coordinates.Min(item => item.Longitude),
            coordinates.Max(item => item.Longitude),
            coordinates.Min(item => item.Latitude),
            coordinates.Max(item => item.Latitude));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
