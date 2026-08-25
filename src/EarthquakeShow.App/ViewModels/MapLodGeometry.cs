using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public enum MapDetailLevel
{
    Overview,
    Medium,
    High,
}

public sealed record MapGeometrySet(
    OfflineMapGeometry Areas,
    OfflineMapGeometry? Municipalities,
    OfflineMapBoundaryGeometry? Boundaries);

public sealed class MapLodResourceProvider
{
    private readonly string _areasPath;
    private readonly string _municipalitiesPath;
    private readonly string _boundariesPath;
    private readonly string? _highAreasPath;
    private readonly string? _highMunicipalitiesPath;
    private readonly string? _highBoundariesPath;

    public MapLodResourceProvider(
        string areasPath,
        string municipalitiesPath,
        string boundariesPath,
        string? highAreasPath = null,
        string? highMunicipalitiesPath = null,
        string? highBoundariesPath = null)
    {
        _areasPath = areasPath ?? throw new ArgumentNullException(nameof(areasPath));
        _municipalitiesPath = municipalitiesPath ??
            throw new ArgumentNullException(nameof(municipalitiesPath));
        _boundariesPath = boundariesPath ??
            throw new ArgumentNullException(nameof(boundariesPath));
        _highAreasPath = highAreasPath;
        _highMunicipalitiesPath = highMunicipalitiesPath;
        _highBoundariesPath = highBoundariesPath;
    }

    public MapGeometrySet LoadMedium(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OfflineMapGeometry areas = OfflineMapGeometry.LoadFromFile(_areasPath);
        cancellationToken.ThrowIfCancellationRequested();
        OfflineMapGeometry municipalities = OfflineMapGeometry.LoadFromFile(_municipalitiesPath);
        cancellationToken.ThrowIfCancellationRequested();
        OfflineMapBoundaryGeometry boundaries = OfflineMapBoundaryGeometry.LoadFromFile(_boundariesPath);
        return new MapGeometrySet(areas, municipalities, boundaries);
    }

    public MapGeometrySet LoadHigh(
        CancellationToken cancellationToken = default,
        MapGeometryBounds? viewportBounds = null)
    {
        if (string.IsNullOrWhiteSpace(_highAreasPath) ||
            string.IsNullOrWhiteSpace(_highMunicipalitiesPath))
        {
            throw new InvalidOperationException("未配置高精度地图资源。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        OfflineMapGeometry areas = OfflineMapGeometry.LoadFromFile(
            _highAreasPath,
            viewportBounds,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        OfflineMapGeometry municipalities = OfflineMapGeometry.LoadFromFile(
            _highMunicipalitiesPath,
            viewportBounds,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        OfflineMapBoundaryGeometry boundaries = string.IsNullOrWhiteSpace(_highBoundariesPath)
            ? OfflineMapBoundaryGeometry.FromPolygons(areas)
            : OfflineMapBoundaryGeometry.LoadFromFile(_highBoundariesPath, viewportBounds);
        return new MapGeometrySet(areas, municipalities, boundaries);
    }
}
