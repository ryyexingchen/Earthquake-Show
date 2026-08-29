using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public sealed record TsunamiMapLine(
    string Code,
    string Name,
    ImmutableArray<GeoCoordinate> Coordinates);

public sealed class TsunamiMapGeometry
{
    private TsunamiMapGeometry(
        ImmutableArray<TsunamiMapLine> lines,
        MapGeometryBounds bounds)
    {
        Lines = lines;
        Bounds = bounds;
    }

    public ImmutableArray<TsunamiMapLine> Lines { get; }

    public MapGeometryBounds Bounds { get; }

    public static TsunamiMapGeometry Empty { get; } = new([], new(122, 146, 24, 46));

    public static TsunamiMapGeometry LoadFromFile(string path, int pointStride = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (pointStride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pointStride));
        }

        if (!File.Exists(path))
        {
            return Empty;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("features", out JsonElement features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("海啸地图 GeoJSON 必须包含 features 数组。");
        }

        var lines = ImmutableArray.CreateBuilder<TsunamiMapLine>();
        foreach (JsonElement feature in features.EnumerateArray())
        {
            JsonElement properties = feature.GetProperty("properties");
            JsonElement geometry = feature.GetProperty("geometry");
            string code = GetString(properties, "forecastAreaCode") ?? string.Empty;
            string name = GetString(properties, "name") ?? code;
            string type = geometry.GetProperty("type").GetString() ?? string.Empty;
            JsonElement coordinates = geometry.GetProperty("coordinates");
            if (type == "LineString")
            {
                AddLine(lines, code, name, coordinates, pointStride);
            }
            else if (type == "MultiLineString" && coordinates.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in coordinates.EnumerateArray())
                {
                    AddLine(lines, code, name, line, pointStride);
                }
            }
            else
            {
                throw new FormatException($"不支持的海啸地图几何类型：{type}。");
            }
        }

        if (lines.Count == 0)
        {
            return Empty;
        }

        ImmutableArray<TsunamiMapLine> result = lines.ToImmutable();
        IEnumerable<GeoCoordinate> points = result.SelectMany(line => line.Coordinates);
        return new(result, new(
            points.Min(point => point.Longitude),
            points.Max(point => point.Longitude),
            points.Min(point => point.Latitude),
            points.Max(point => point.Latitude)));
    }

    private static void AddLine(
        ImmutableArray<TsunamiMapLine>.Builder lines,
        string code,
        string name,
        JsonElement coordinates,
        int pointStride)
    {
        if (coordinates.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var points = ImmutableArray.CreateBuilder<GeoCoordinate>();
        int index = 0;
        JsonElement lastCoordinate = default;
        foreach (JsonElement coordinate in coordinates.EnumerateArray())
        {
            lastCoordinate = coordinate;
            if (coordinate.ValueKind != JsonValueKind.Array || coordinate.GetArrayLength() < 2)
            {
                index++;
                continue;
            }

            double longitude = coordinate[0].GetDouble();
            double latitude = coordinate[1].GetDouble();
            if (double.IsFinite(longitude) && double.IsFinite(latitude) &&
                (index % pointStride == 0 || index == coordinates.GetArrayLength() - 1))
            {
                points.Add(new GeoCoordinate(latitude, longitude));
            }

            index++;
        }

        if (pointStride > 1 && lastCoordinate.ValueKind == JsonValueKind.Array &&
            lastCoordinate.GetArrayLength() >= 2)
        {
            double longitude = lastCoordinate[0].GetDouble();
            double latitude = lastCoordinate[1].GetDouble();
            GeoCoordinate endpoint = new(latitude, longitude);
            if (double.IsFinite(longitude) && double.IsFinite(latitude) &&
                !points.Contains(endpoint))
            {
                points.Add(endpoint);
            }
        }

        if (points.Count >= 2)
        {
            lines.Add(new TsunamiMapLine(code, name, points.ToImmutable()));
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
