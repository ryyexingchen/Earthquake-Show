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
        MapGeometrySet? geometrySet = TryLoadMedium(cancellationToken);
        return geometrySet ?? throw new OperationCanceledException(cancellationToken);
    }

    public MapGeometrySet? TryLoadMedium(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OfflineMapGeometry areas = OfflineMapGeometry.LoadFromFile(_areasPath);
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OfflineMapGeometry municipalities = OfflineMapGeometry.LoadFromFile(_municipalitiesPath);
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OfflineMapBoundaryGeometry boundaries = OfflineMapBoundaryGeometry.LoadFromFile(_boundariesPath);
        return new MapGeometrySet(areas, municipalities, boundaries);
    }

    public MapGeometrySet LoadHigh(
        CancellationToken cancellationToken = default,
        MapGeometryBounds? viewportBounds = null)
    {
        MapGeometrySet? geometrySet = TryLoadHigh(cancellationToken, viewportBounds);
        return geometrySet ?? throw new OperationCanceledException(cancellationToken);
    }

    public MapGeometrySet? TryLoadHigh(
        CancellationToken cancellationToken = default,
        MapGeometryBounds? viewportBounds = null)
    {
        if (string.IsNullOrWhiteSpace(_highAreasPath) ||
            string.IsNullOrWhiteSpace(_highMunicipalitiesPath))
        {
            throw new InvalidOperationException("未配置高精度地图资源。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OfflineMapGeometry? areas = OfflineMapGeometry.TryLoadFromFile(
            _highAreasPath,
            viewportBounds,
            cancellationToken);
        if (areas is null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OfflineMapGeometry? municipalities = OfflineMapGeometry.TryLoadFromFile(
            _highMunicipalitiesPath,
            viewportBounds,
            cancellationToken);
        if (municipalities is null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OfflineMapBoundaryGeometry boundaries = string.IsNullOrWhiteSpace(_highBoundariesPath)
            ? OfflineMapBoundaryGeometry.FromPolygons(areas)
            : OfflineMapBoundaryGeometry.LoadFromFile(_highBoundariesPath, viewportBounds);
        return new MapGeometrySet(areas, municipalities, boundaries);
    }
}
