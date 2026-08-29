using System.Collections.Immutable;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

/// <summary>
/// JMA 海啸观测点与近海发布点目录。所有 code 均按字符串保存，保留前导零。
/// </summary>
public sealed class JmaTsunamiStationCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ImmutableDictionary<string, JmaTsunamiStationCatalogEntry> _stations;
    private readonly ImmutableDictionary<string, JmaTsunamiPublicationCatalogEntry> _publications;
    private readonly ImmutableDictionary<string, ImmutableArray<JmaTsunamiPublicationCatalogEntry>> _publicationsByStation;

    private JmaTsunamiStationCatalog(
        string sourceVersion,
        IEnumerable<JmaTsunamiStationCatalogEntry> stations,
        IEnumerable<JmaTsunamiPublicationCatalogEntry> publications)
    {
        SourceVersion = sourceVersion;
        _stations = stations
            .Where(item => !string.IsNullOrWhiteSpace(item.StationCode))
            .ToImmutableDictionary(item => item.StationCode, StringComparer.Ordinal);
        _publications = publications
            .Where(item => !string.IsNullOrWhiteSpace(item.PublicationCode))
            .ToImmutableDictionary(item => item.PublicationCode, StringComparer.Ordinal);
        _publicationsByStation = _publications.Values
            .SelectMany(publication => publication.StationCodes.Select(code => (code, publication)))
            .GroupBy(item => item.code, StringComparer.Ordinal)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.Select(item => item.publication).ToImmutableArray(),
                StringComparer.Ordinal);
    }

    public string SourceVersion { get; }

    public static JmaTsunamiStationCatalog Empty { get; } = new("", [], []);

    public ImmutableArray<JmaTsunamiStationCatalogEntry> Stations => _stations.Values.ToImmutableArray();

    public ImmutableArray<JmaTsunamiPublicationCatalogEntry> Publications => _publications.Values.ToImmutableArray();

    public bool TryGetStation(string stationCode, out JmaTsunamiStationCatalogEntry station) =>
        _stations.TryGetValue(stationCode, out station!);

    public bool TryGetPublication(string publicationCode, out JmaTsunamiPublicationCatalogEntry publication) =>
        _publications.TryGetValue(publicationCode, out publication!);

    public ImmutableArray<JmaTsunamiPublicationCatalogEntry> GetPublicationsForStation(string stationCode) =>
        _publicationsByStation.TryGetValue(stationCode, out ImmutableArray<JmaTsunamiPublicationCatalogEntry> publications)
            ? publications
            : [];

    public static JmaTsunamiStationCatalog LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadJson(File.ReadAllText(path), path);
    }

    public static JmaTsunamiStationCatalog LoadJson(string json, string sourceVersion = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JmaTsunamiCatalogDocument? document = JsonSerializer.Deserialize<JmaTsunamiCatalogDocument>(json, JsonOptions);
        if (document is null)
        {
            throw new FormatException("海啸观测点目录为空。");
        }

        return new(
            string.IsNullOrWhiteSpace(document.SourceVersion) ? sourceVersion : document.SourceVersion,
            document.Stations,
            document.OffshorePublicationMappings);
    }

    public static JmaTsunamiStationCatalog Create(
        string sourceVersion,
        IEnumerable<JmaTsunamiStationCatalogEntry> stations,
        IEnumerable<JmaTsunamiPublicationCatalogEntry> publications)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(publications);
        return new(sourceVersion ?? string.Empty, stations, publications);
    }
}

public sealed record JmaTsunamiStationCatalogEntry(
    string StationCode,
    string Name,
    string? NameKana,
    double? Latitude,
    double? Longitude,
    string? ForecastAreaCode);

public sealed record JmaTsunamiPublicationCatalogEntry(
    string PublicationCode,
    string Name,
    string? NameKana,
    ImmutableArray<string> StationCodes);

internal sealed class JmaTsunamiCatalogDocument
{
    public string SourceVersion { get; set; } = string.Empty;

    public List<JmaTsunamiStationCatalogEntry> Stations { get; set; } = [];

    public List<JmaTsunamiPublicationCatalogEntry> OffshorePublicationMappings { get; set; } = [];
}
