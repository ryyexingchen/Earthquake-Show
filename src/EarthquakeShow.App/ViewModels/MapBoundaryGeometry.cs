using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record EarthquakeMapBoundary(
    string AreaCode1,
    string AreaCode2,
    ImmutableArray<GeoCoordinate> Coordinates);

public sealed class OfflineMapBoundaryGeometry
{
    private OfflineMapBoundaryGeometry(
        ImmutableArray<EarthquakeMapBoundary> boundaries,
        ImmutableDictionary<string, ImmutableArray<EarthquakeMapBoundary>> boundariesByArea,
        string source,
        string sourceVersion,
        bool isOfficialBoundary,
        int invalidGeometryCount,
        int topologyPrecision,
        double simplificationToleranceDegrees,
        double minRingAreaDegreesSquared,
        MapGeometryBounds bounds)
    {
        Boundaries = boundaries;
        BoundariesByArea = boundariesByArea;
        Source = source;
        SourceVersion = sourceVersion;
        IsOfficialBoundary = isOfficialBoundary;
        InvalidGeometryCount = invalidGeometryCount;
        TopologyPrecision = topologyPrecision;
        SimplificationToleranceDegrees = simplificationToleranceDegrees;
        MinRingAreaDegreesSquared = minRingAreaDegreesSquared;
        Bounds = bounds;
    }

    public ImmutableArray<EarthquakeMapBoundary> Boundaries { get; }

    public ImmutableDictionary<string, ImmutableArray<EarthquakeMapBoundary>> BoundariesByArea { get; }

    public string Source { get; }

    public string SourceVersion { get; }

    public bool IsOfficialBoundary { get; }

    public int InvalidGeometryCount { get; }

    public int TopologyPrecision { get; }

    public double SimplificationToleranceDegrees { get; }

    public double MinRingAreaDegreesSquared { get; }

    public MapGeometryBounds Bounds { get; }

    public IReadOnlyList<EarthquakeMapBoundary> GetForArea(string? areaCode)
    {
        return !string.IsNullOrWhiteSpace(areaCode) &&
            BoundariesByArea.TryGetValue(areaCode.Trim(), out ImmutableArray<EarthquakeMapBoundary> boundaries)
            ? boundaries
            : [];
    }

    public static OfflineMapBoundaryGeometry LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("features", out JsonElement features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("边界 GeoJSON 必须包含 features 数组。");
        }

        var boundaries = ImmutableArray.CreateBuilder<EarthquakeMapBoundary>();
        var points = ImmutableArray.CreateBuilder<GeoCoordinate>();
        var boundariesByArea = new Dictionary<string, List<EarthquakeMapBoundary>>(
            StringComparer.Ordinal);
        int invalidGeometryCount = 0;

        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out JsonElement properties) ||
                properties.ValueKind != JsonValueKind.Object ||
                !feature.TryGetProperty("geometry", out JsonElement geometry) ||
                geometry.ValueKind != JsonValueKind.Object)
            {
                invalidGeometryCount++;
                continue;
            }

            string areaCode1 = GetString(properties, "areaCode1")?.Trim() ?? string.Empty;
            string areaCode2 = GetString(properties, "areaCode2")?.Trim() ?? string.Empty;
            if (areaCode1.Length == 0 || areaCode1 == areaCode2)
            {
                invalidGeometryCount++;
                continue;
            }

            string geometryType = GetString(geometry, "type") ?? string.Empty;
            if (!geometry.TryGetProperty("coordinates", out JsonElement coordinates))
            {
                invalidGeometryCount++;
                continue;
            }

            int beforeCount = boundaries.Count;
            switch (geometryType)
            {
                case "LineString":
                    AddLine(
                        boundaries,
                        points,
                        boundariesByArea,
                        areaCode1,
                        areaCode2,
                        coordinates);
                    break;
                case "MultiLineString":
                    if (coordinates.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement line in coordinates.EnumerateArray())
                        {
                            AddLine(
                                boundaries,
                                points,
                                boundariesByArea,
                                areaCode1,
                                areaCode2,
                                line);
                        }
                    }
                    break;
                default:
                    throw new FormatException($"不支持的边界 GeoJSON 几何类型：{geometryType}。");
            }

            if (boundaries.Count == beforeCount)
            {
                invalidGeometryCount++;
            }
        }

        if (boundaries.Count == 0 || points.Count == 0)
        {
            throw new FormatException("边界 GeoJSON 没有可用的 LineString。");
        }

        JsonElement metadata = root.TryGetProperty("metadata", out JsonElement metadataElement) &&
            metadataElement.ValueKind == JsonValueKind.Object
            ? metadataElement
            : default;
        var immutableIndex = boundariesByArea.ToImmutableDictionary(
            item => item.Key,
            item => item.Value.ToImmutableArray(),
            StringComparer.Ordinal);
        MapGeometryBounds bounds = CalculateBounds(points);
        return new OfflineMapBoundaryGeometry(
            boundaries.ToImmutable(),
            immutableIndex,
            GetString(metadata, "source") ?? "未注明来源",
            GetString(metadata, "sourceVersion") ?? "unknown",
            GetBoolean(metadata, "officialBoundary"),
            invalidGeometryCount,
            GetInt32(metadata, "topologyPrecision"),
            GetDouble(metadata, "simplificationToleranceDegrees"),
            GetDouble(metadata, "minRingAreaDegreesSquared"),
            bounds);
    }

    public static OfflineMapBoundaryGeometry LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    private static void AddLine(
        ImmutableArray<EarthquakeMapBoundary>.Builder boundaries,
        ImmutableArray<GeoCoordinate>.Builder points,
        Dictionary<string, List<EarthquakeMapBoundary>> boundariesByArea,
        string areaCode1,
        string areaCode2,
        JsonElement coordinates)
    {
        if (!TryReadLine(coordinates, out ImmutableArray<GeoCoordinate> line))
        {
            return;
        }

        var boundary = new EarthquakeMapBoundary(areaCode1, areaCode2, line);
        boundaries.Add(boundary);
        points.AddRange(line);
        AddToIndex(boundariesByArea, areaCode1, boundary);
        if (areaCode2.Length > 0)
        {
            AddToIndex(boundariesByArea, areaCode2, boundary);
        }
    }

    private static void AddToIndex(
        Dictionary<string, List<EarthquakeMapBoundary>> boundariesByArea,
        string areaCode,
        EarthquakeMapBoundary boundary)
    {
        if (!boundariesByArea.TryGetValue(areaCode, out List<EarthquakeMapBoundary>? boundaries))
        {
            boundaries = [];
            boundariesByArea.Add(areaCode, boundaries);
        }

        boundaries.Add(boundary);
    }

    private static bool TryReadLine(
        JsonElement coordinates,
        out ImmutableArray<GeoCoordinate> line)
    {
        var points = ImmutableArray.CreateBuilder<GeoCoordinate>();
        if (coordinates.ValueKind != JsonValueKind.Array)
        {
            line = ImmutableArray<GeoCoordinate>.Empty;
            return false;
        }

        foreach (JsonElement coordinate in coordinates.EnumerateArray())
        {
            if (coordinate.ValueKind != JsonValueKind.Array ||
                coordinate.GetArrayLength() < 2 ||
                !TryGetDouble(coordinate[0], out double longitude) ||
                !TryGetDouble(coordinate[1], out double latitude) ||
                !double.IsFinite(longitude) ||
                !double.IsFinite(latitude) ||
                longitude is < -180 or > 180 ||
                latitude is < -90 or > 90)
            {
                line = ImmutableArray<GeoCoordinate>.Empty;
                return false;
            }

            points.Add(new GeoCoordinate(latitude, longitude));
        }

        line = points.Count >= 2 ? points.ToImmutable() : ImmutableArray<GeoCoordinate>.Empty;
        return line.Length >= 2;
    }

    private static MapGeometryBounds CalculateBounds(
        ImmutableArray<GeoCoordinate>.Builder points)
    {
        return new MapGeometryBounds(
            points.Min(item => item.Longitude),
            points.Max(item => item.Longitude),
            points.Min(item => item.Latitude),
            points.Max(item => item.Latitude));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement value) &&
            value.TryGetInt32(out int number)
            ? number
            : 0;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement value) &&
            value.TryGetDouble(out double number) &&
            double.IsFinite(number)
            ? number
            : 0;
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value);
    }
}
