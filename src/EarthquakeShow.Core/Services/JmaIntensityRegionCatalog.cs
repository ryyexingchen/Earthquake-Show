using System.Collections.Immutable;
using System.Text.Json;

namespace EarthquakeShow.Core.Services;

public sealed record JmaIntensityPrefectureDefinition(string Code, string Name);

public sealed record JmaIntensityAreaDefinition(
    string Code,
    string Name,
    string PrefectureCode,
    string PrefectureName);

public sealed record JmaIntensityMunicipalityDefinition(
    string Code,
    string Name,
    string PrefectureCode,
    string PrefectureName,
    string AreaCode,
    string AreaName,
    ImmutableArray<string> Aliases);

public sealed class JmaIntensityRegionCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ImmutableArray<JmaIntensityPrefectureDefinition> _prefectures;
    private readonly ImmutableArray<JmaIntensityAreaDefinition> _areas;
    private readonly ImmutableArray<JmaIntensityMunicipalityDefinition> _municipalities;
    private readonly IReadOnlyDictionary<string, JmaIntensityAreaDefinition> _areasByCode;
    private readonly IReadOnlyDictionary<string, JmaIntensityPrefectureDefinition> _prefecturesByCode;

    private JmaIntensityRegionCatalog(
        IEnumerable<JmaIntensityPrefectureDefinition> prefectures,
        IEnumerable<JmaIntensityAreaDefinition> areas,
        IEnumerable<JmaIntensityMunicipalityDefinition> municipalities)
    {
        _prefectures = prefectures.ToImmutableArray();
        _areas = areas.ToImmutableArray();
        _municipalities = municipalities.ToImmutableArray();
        _prefecturesByCode = _prefectures.ToDictionary(item => item.Code, StringComparer.Ordinal);
        _areasByCode = _areas.ToDictionary(item => item.Code, StringComparer.Ordinal);
    }

    public ImmutableArray<JmaIntensityPrefectureDefinition> Prefectures => _prefectures;

    public ImmutableArray<JmaIntensityAreaDefinition> Areas => _areas;

    public ImmutableArray<JmaIntensityMunicipalityDefinition> Municipalities => _municipalities;

    public static JmaIntensityRegionCatalog Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CatalogDocument document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
            ?? throw new FormatException("震度层级目录不能为空。");
        return new(
            document.Prefectures ?? [],
            document.Areas ?? [],
            (document.Municipalities ?? [])
                .Select(item => new JmaIntensityMunicipalityDefinition(
                    item.Code,
                    item.Name,
                    item.PrefectureCode,
                    item.PrefectureName,
                    item.AreaCode,
                    item.AreaName,
                    (item.Aliases ?? []).ToImmutableArray())));
    }

    public static JmaIntensityRegionCatalog LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path));
    }

    public bool TryGetPrefecture(string code, out JmaIntensityPrefectureDefinition prefecture) =>
        _prefecturesByCode.TryGetValue(code, out prefecture!);

    public bool TryGetArea(string code, out JmaIntensityAreaDefinition area) =>
        _areasByCode.TryGetValue(code, out area!);

    public bool TryResolveAreaName(string name, out JmaIntensityAreaDefinition area)
    {
        area = _areas.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal))!;
        return area is not null;
    }

    public bool TryResolveMunicipality(
        string prefectureName,
        string address,
        out JmaIntensityMunicipalityDefinition municipality)
    {
        string normalizedAddress = address.Trim();
        string withoutPrefecture = normalizedAddress.StartsWith(prefectureName, StringComparison.Ordinal)
            ? normalizedAddress[prefectureName.Length..]
            : normalizedAddress;
        municipality = _municipalities
            .Where(item => string.Equals(item.PrefectureName, prefectureName, StringComparison.Ordinal))
            .Where(item => item.Aliases.Any(alias =>
                alias.Length > 0 && withoutPrefecture.StartsWith(alias, StringComparison.Ordinal)))
            .OrderByDescending(item => item.Aliases.Max(alias => alias.Length))
            .FirstOrDefault()!;
        return municipality is not null;
    }

    private sealed record CatalogDocument(
        JmaIntensityPrefectureDefinition[]? Prefectures,
        JmaIntensityAreaDefinition[]? Areas,
        MunicipalityDocument[]? Municipalities);

    private sealed record MunicipalityDocument(
        string Code,
        string Name,
        string PrefectureCode,
        string PrefectureName,
        string AreaCode,
        string AreaName,
        string[]? Aliases);
}
