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

    public static OfflineMapGeometry LoadFromJson(
        string json,
        MapGeometryBounds? filterBounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        return LoadFromDocument(document, filterBounds);
    }

    public static OfflineMapGeometry LoadFromFile(
        string path,
        MapGeometryBounds? filterBounds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        if (filterBounds is MapGeometryBounds bounds &&
            MapGeometryFeatureIndexReader.TryLoad(path, stream.Length, out MapGeometryFeatureIndexFile? index))
        {
            return LoadFromIndexedFile(stream, index, bounds, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument document = JsonDocument.Parse(stream);
        return LoadFromDocument(document, filterBounds);
    }

    private static OfflineMapGeometry LoadFromIndexedFile(
        FileStream stream,
        MapGeometryFeatureIndexFile index,
        MapGeometryBounds filterBounds,
        CancellationToken cancellationToken)
    {
        var polygons = ImmutableArray.CreateBuilder<MapPolygonGeometry>();
        int invalidGeometryCount = 0;
        foreach (MapGeometryFeatureIndexEntry entry in index.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Intersects(
                    entry.MinLongitude,
                    entry.MaxLongitude,
                    entry.MinLatitude,
                    entry.MaxLatitude,
                    filterBounds))
            {
                continue;
            }

            stream.Position = entry.Offset;
            byte[] featureBytes = GC.AllocateUninitializedArray<byte>(entry.Length);
            stream.ReadExactly(featureBytes);
            using JsonDocument featureDocument = JsonDocument.Parse(featureBytes);
            if (!TryReadFeature(
                    featureDocument.RootElement,
                    polygons,
                    filterBounds,
                    index.OfficialBoundary,
                    ref invalidGeometryCount))
            {
                invalidGeometryCount++;
            }
        }

        if (polygons.Count == 0)
        {
            return new OfflineMapGeometry(
                [],
                string.IsNullOrWhiteSpace(index.Source) ? "未注明来源" : index.Source,
                false,
                invalidGeometryCount,
                filterBounds);
        }

        return new OfflineMapGeometry(
            polygons.ToImmutable(),
            string.IsNullOrWhiteSpace(index.Source) ? "未注明来源" : index.Source,
            polygons.All(item => item.IsOfficialBoundary),
            invalidGeometryCount,
            CalculateBounds(polygons));
    }

    private static OfflineMapGeometry LoadFromDocument(
        JsonDocument document,
        MapGeometryBounds? filterBounds)
    {
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("features", out JsonElement features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("GeoJSON 必须包含 features 数组。");
        }

        var polygons = ImmutableArray.CreateBuilder<MapPolygonGeometry>();
        int invalidGeometryCount = 0;
        bool defaultOfficialBoundary = root.TryGetProperty(
                "metadata",
                out JsonElement metadataElement) &&
            metadataElement.TryGetProperty(
                "officialBoundary",
                out JsonElement officialBoundaryElement) &&
            officialBoundaryElement.ValueKind == JsonValueKind.True;
        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (!TryReadFeature(
                    feature,
                    polygons,
                    filterBounds,
                    defaultOfficialBoundary,
                    ref invalidGeometryCount))
            {
                invalidGeometryCount++;
            }
        }

        if (polygons.Count == 0 && filterBounds is null)
        {
            throw new FormatException("GeoJSON 没有可绘制的多边形。");
        }

        string source = root.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.TryGetProperty("source", out JsonElement sourceElement)
            ? sourceElement.GetString() ?? "未注明来源"
            : "未注明来源";
        bool allOfficial = polygons.Count > 0 && polygons.All(item => item.IsOfficialBoundary);
        MapGeometryBounds bounds = polygons.Count > 0
            ? CalculateBounds(polygons)
            : filterBounds!.Value;
        return new OfflineMapGeometry(
            polygons.ToImmutable(),
            source,
            allOfficial,
            invalidGeometryCount,
            bounds);
    }

    private static bool TryReadFeature(
        JsonElement feature,
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        MapGeometryBounds? filterBounds,
        bool defaultOfficialBoundary,
        ref int invalidGeometryCount)
    {
        if (!feature.TryGetProperty("properties", out JsonElement properties) ||
            !feature.TryGetProperty("geometry", out JsonElement geometry))
        {
            return false;
        }

        string code = GetString(properties, "code") ??
            GetString(properties, "areaCode") ??
            GetString(properties, "municipalityCode") ?? string.Empty;
        string name = GetString(properties, "name") ?? code;
        bool officialBoundary = properties.TryGetProperty(
                "officialBoundary",
                out JsonElement officialElement)
            ? officialElement.ValueKind == JsonValueKind.True
            : defaultOfficialBoundary;

        string geometryType = geometry.GetProperty("type").GetString() ?? string.Empty;
        JsonElement coordinates = geometry.GetProperty("coordinates");
        switch (geometryType)
        {
            case "Polygon":
                if (!AddPolygon(
                        polygons,
                        code,
                        name,
                        coordinates,
                        officialBoundary,
                        filterBounds))
                {
                    invalidGeometryCount++;
                }

                return true;
            case "MultiPolygon":
                if (!AddMultiPolygon(
                        polygons,
                        code,
                        name,
                        coordinates,
                        officialBoundary,
                        filterBounds))
                {
                    invalidGeometryCount++;
                }

                return true;
            default:
                throw new FormatException($"不支持的 GeoJSON 几何类型：{geometryType}。");
        }
    }

    private static bool AddPolygon(
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        string code,
        string name,
        JsonElement polygonCoordinates,
        bool officialBoundary,
        MapGeometryBounds? filterBounds)
    {
        if (polygonCoordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var rings = ImmutableArray.CreateBuilder<ImmutableArray<GeoCoordinate>>();
        ReadRings(polygonCoordinates, rings);
        return AddRings(polygons, code, name, rings, officialBoundary, filterBounds);
    }

    private static bool AddMultiPolygon(
        ImmutableArray<MapPolygonGeometry>.Builder polygons,
        string code,
        string name,
        JsonElement multiPolygonCoordinates,
        bool officialBoundary,
        MapGeometryBounds? filterBounds)
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

        return AddRings(polygons, code, name, rings, officialBoundary, filterBounds);
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
        bool officialBoundary,
        MapGeometryBounds? filterBounds)
    {
        if (rings.Count == 0)
        {
            return false;
        }

        if (filterBounds is MapGeometryBounds bounds &&
            !rings.Any(ring => Intersects(ring, bounds)))
        {
            return true;
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

    private static bool Intersects(
        ImmutableArray<GeoCoordinate> ring,
        MapGeometryBounds bounds)
    {
        if (ring.IsDefaultOrEmpty)
        {
            return false;
        }

        return ring.Min(item => item.Longitude) <= bounds.MaxLongitude &&
            ring.Max(item => item.Longitude) >= bounds.MinLongitude &&
            ring.Min(item => item.Latitude) <= bounds.MaxLatitude &&
            ring.Max(item => item.Latitude) >= bounds.MinLatitude;
    }

    private static bool Intersects(
        double minLongitude,
        double maxLongitude,
        double minLatitude,
        double maxLatitude,
        MapGeometryBounds bounds)
    {
        return minLongitude <= bounds.MaxLongitude &&
            maxLongitude >= bounds.MinLongitude &&
            minLatitude <= bounds.MaxLatitude &&
            maxLatitude >= bounds.MinLatitude;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
