using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public enum JmaStationCoordinateMatchKind
{
    None = 0,
    Code = 1,
    UniqueName = 2,
}

public sealed record JmaStationCatalogEntry(
    string? StationCode,
    string Name,
    GeoCoordinate? Coordinate,
    string PrefectureCode,
    string? MunicipalityCode,
    string Affiliation);

public sealed record JmaStationCatalogDiagnostics(
    int EntryCount,
    int CoordinateCount,
    int CodeCount,
    int MissingCodeCount,
    int MissingCoordinateCount,
    int DuplicateCodeCount,
    int DuplicateNameCount,
    int SupplementalCodeCount);

public sealed class JmaStationCoordinateCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ImmutableDictionary<string, GeoCoordinate> _coordinatesByCode;
    private readonly ImmutableDictionary<string, GeoCoordinate> _coordinatesByUniqueName;

    private JmaStationCoordinateCatalog(
        ImmutableArray<JmaStationCatalogEntry> entries,
        IReadOnlyDictionary<string, GeoCoordinate>? supplementalCodeCoordinates)
    {
        Entries = entries;

        var coordinatesByCode = ImmutableDictionary.CreateBuilder<string, GeoCoordinate>(StringComparer.Ordinal);
        var codeGroups = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.StationCode))
            .GroupBy(entry => entry.StationCode!.Trim(), StringComparer.Ordinal)
            .ToArray();
        foreach (IGrouping<string, JmaStationCatalogEntry> group in
                 codeGroups.Where(group => group.Count() == 1 && group.Single().Coordinate is not null))
        {
            coordinatesByCode.Add(group.Key, group.Single().Coordinate!.Value);
        }

        int supplementalCodeCount = 0;
        if (supplementalCodeCoordinates is not null)
        {
            foreach ((string code, GeoCoordinate coordinate) in supplementalCodeCoordinates)
            {
                if (coordinatesByCode.TryAdd(code, coordinate))
                {
                    supplementalCodeCount++;
                }
            }
        }

        var coordinatesByUniqueName = ImmutableDictionary.CreateBuilder<string, GeoCoordinate>(StringComparer.Ordinal);
        var nameGroups = entries
            .Select(entry => (Name: NormalizeName(entry.Name), entry.Coordinate))
            .Where(entry => entry.Name.Length > 0)
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (IGrouping<string, (string Name, GeoCoordinate? Coordinate)> group in
                 nameGroups.Where(group => group.Count() == 1 && group.Single().Coordinate is not null))
        {
            coordinatesByUniqueName.Add(group.Key, group.Single().Coordinate!.Value);
        }

        _coordinatesByCode = coordinatesByCode.ToImmutable();
        _coordinatesByUniqueName = coordinatesByUniqueName.ToImmutable();
        Diagnostics = new JmaStationCatalogDiagnostics(
            entries.Length,
            entries.Count(entry => entry.Coordinate is not null),
            entries.Count(entry => !string.IsNullOrWhiteSpace(entry.StationCode)),
            entries.Count(entry => string.IsNullOrWhiteSpace(entry.StationCode)),
            entries.Count(entry => entry.Coordinate is null),
            codeGroups.Count(group => group.Count() > 1),
            nameGroups.Count(group => group.Count() > 1),
            supplementalCodeCount);
    }

    public ImmutableArray<JmaStationCatalogEntry> Entries { get; }

    public JmaStationCatalogDiagnostics Diagnostics { get; }

    public string DatasetVersion { get; private init; } = string.Empty;

    public string SourceUrl { get; private init; } = string.Empty;

    public string RetrievedDate { get; private init; } = string.Empty;

    public string CoordinateReferenceSystem { get; private init; } = string.Empty;

    public string StationCodeStatus { get; private init; } = string.Empty;

    public static JmaStationCoordinateCatalog LoadFile(
        string path,
        IReadOnlyDictionary<string, GeoCoordinate>? supplementalCodeCoordinates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadJson(File.ReadAllText(path, Encoding.UTF8), supplementalCodeCoordinates);
    }

    public static JmaStationCoordinateCatalog LoadJson(
        string json,
        IReadOnlyDictionary<string, GeoCoordinate>? supplementalCodeCoordinates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        StationCatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<StationCatalogDocument>(json, JsonOptions)
                ?? throw new FormatException("JMA 观测点目录不能为空。");
        }
        catch (JsonException exception)
        {
            throw new FormatException("JMA 观测点目录 JSON 格式无效。", exception);
        }

        if (document.SchemaVersion != 1)
        {
            throw new FormatException($"不支持的 JMA 观测点目录版本：{document.SchemaVersion}。");
        }

        if (document.Stations is null || document.Stations.Length == 0)
        {
            throw new FormatException("JMA 观测点目录缺少观测点记录。");
        }

        ImmutableArray<JmaStationCatalogEntry> entries = document.Stations
            .Select(ToEntry)
            .ToImmutableArray();
        return new JmaStationCoordinateCatalog(entries, supplementalCodeCoordinates)
        {
            DatasetVersion = document.DatasetVersion?.Trim() ?? string.Empty,
            SourceUrl = document.SourceUrl?.Trim() ?? string.Empty,
            RetrievedDate = document.RetrievedDate?.Trim() ?? string.Empty,
            CoordinateReferenceSystem = document.CoordinateReferenceSystem?.Trim() ?? string.Empty,
            StationCodeStatus = document.StationCodeStatus?.Trim() ?? string.Empty,
        };
    }

    public static JmaStationCoordinateCatalog FromCodeCoordinates(
        IReadOnlyDictionary<string, GeoCoordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        ImmutableArray<JmaStationCatalogEntry> entries = coordinates
            .Select(pair => new JmaStationCatalogEntry(
                pair.Key,
                pair.Key,
                pair.Value,
                string.Empty,
                null,
                string.Empty))
            .ToImmutableArray();
        return new JmaStationCoordinateCatalog(entries, null)
        {
            DatasetVersion = "fixed-csv-fallback",
        };
    }

    public bool TryResolve(
        string? stationCode,
        string? stationName,
        out GeoCoordinate coordinate,
        out JmaStationCoordinateMatchKind matchKind)
    {
        if (!string.IsNullOrWhiteSpace(stationCode) &&
            _coordinatesByCode.TryGetValue(stationCode.Trim(), out coordinate))
        {
            matchKind = JmaStationCoordinateMatchKind.Code;
            return true;
        }

        string normalizedName = NormalizeName(stationName);
        if (normalizedName.Length > 0 &&
            _coordinatesByUniqueName.TryGetValue(normalizedName, out coordinate))
        {
            matchKind = JmaStationCoordinateMatchKind.UniqueName;
            return true;
        }

        coordinate = default;
        matchKind = JmaStationCoordinateMatchKind.None;
        return false;
    }

    public static string NormalizeName(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('＊', '*').Trim();
    }

    private static JmaStationCatalogEntry ToEntry(StationCatalogRow row)
    {
        GeoCoordinate? coordinate = null;
        if (row.Latitude is double latitude &&
            row.Longitude is double longitude &&
            double.IsFinite(latitude) &&
            double.IsFinite(longitude) &&
            latitude is >= -90 and <= 90 &&
            longitude is >= -180 and <= 180)
        {
            coordinate = new GeoCoordinate(latitude, longitude);
        }

        return new JmaStationCatalogEntry(
            NullIfWhiteSpace(row.StationCode),
            row.Name?.Trim() ?? string.Empty,
            coordinate,
            row.PrefectureCode?.Trim() ?? string.Empty,
            NullIfWhiteSpace(row.MunicipalityCode),
            row.Affiliation?.Trim() ?? string.Empty);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record StationCatalogDocument(
        int SchemaVersion,
        string? DatasetVersion,
        string? SourceUrl,
        string? RetrievedDate,
        string? CoordinateReferenceSystem,
        string? StationCodeStatus,
        StationCatalogRow[]? Stations);

    private sealed record StationCatalogRow(
        string? StationCode,
        string? Name,
        double? Latitude,
        double? Longitude,
        string? PrefectureCode,
        string? MunicipalityCode,
        string? Affiliation);
}
