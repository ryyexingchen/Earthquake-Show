namespace EarthquakeShow.Core.Models;

public sealed record IntensityArea(
    string Code,
    string Name,
    string PrefectureCode,
    string PrefectureName,
    JmaIntensity MaxIntensity);

public sealed record IntensityMunicipality(
    string Code,
    string Name,
    string AreaCode,
    JmaIntensity MaxIntensity);

public sealed record IntensityStation(
    string Code,
    string Name,
    string MunicipalityCode,
    JmaIntensity Intensity,
    GeoCoordinate? Coordinate);
