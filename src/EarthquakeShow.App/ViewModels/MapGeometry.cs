using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record MapPolygonGeometry(
    string Code,
    string Name,
    ImmutableArray<GeoCoordinate> Coordinates,
    bool IsOfficialBoundary);

public sealed class OfflineMapGeometry
{
    private OfflineMapGeometry(
        ImmutableArray<MapPolygonGeometry> polygons,
        string source,
        bool isOfficialBoundary)
    {
        Polygons = polygons;
        Source = source;
        IsOfficialBoundary = isOfficialBoundary;
    }

    public ImmutableArray<MapPolygonGeometry> Polygons { get; }

    public string Source { get; }

    public bool IsOfficialBoundary { get; }

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
                    AddPolygon(polygons, code, name, coordinates, officialBoundary);
                    break;
                case "MultiPolygon":
                    foreach (JsonElement polygon in coordinates.EnumerateArray())
                    {
                        AddPolygon(polygons, code, name, polygon, officialBoundary);
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
        return new OfflineMapGeometry(polygons.ToImmutable(), source, allOfficial);
    }

    public static OfflineMapGeometry LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    private static void AddPolygon(
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        string code,
        string name,
        JsonElement polygonCoordinates,
        bool officialBoundary)
    {
        JsonElement ring = polygonCoordinates.EnumerateArray().FirstOrDefault();
        if (ring.ValueKind != JsonValueKind.Array)
        {
            return;
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
            points.Add(new GeoCoordinate(latitude, longitude));
        }

        if (points.Count >= 3)
        {
            polygons.Add(new MapPolygonGeometry(
                code,
                name,
                points.ToImmutable(),
                officialBoundary));
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
