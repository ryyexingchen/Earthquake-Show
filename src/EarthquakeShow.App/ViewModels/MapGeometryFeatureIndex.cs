using System.IO;
using System.Text.Json;

namespace EarthquakeShow.App.ViewModels;

internal sealed record MapGeometryFeatureIndexFile(
    int Version,
    long SourceLength,
    string Source,
    bool OfficialBoundary,
    IReadOnlyList<MapGeometryFeatureIndexEntry> Features);

internal sealed record MapGeometryFeatureIndexEntry(
    long Offset,
    int Length,
    double MinLongitude,
    double MaxLongitude,
    double MinLatitude,
    double MaxLatitude);

internal static class MapGeometryFeatureIndexReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryLoad(
        string path,
        long sourceLength,
        out MapGeometryFeatureIndexFile index)
    {
        index = null!;
        string indexPath = path + ".index.json";
        if (!File.Exists(indexPath))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(indexPath);
            MapGeometryFeatureIndexFile? candidate =
                JsonSerializer.Deserialize<MapGeometryFeatureIndexFile>(
                    json,
                    SerializerOptions);
            if (candidate is null ||
                candidate.Version != 1 ||
                candidate.SourceLength != sourceLength ||
                candidate.Features is null ||
                candidate.Features.Any(entry =>
                    entry.Offset < 0 ||
                    entry.Length <= 0 ||
                    entry.Offset > sourceLength - entry.Length))
            {
                return false;
            }

            index = candidate;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
