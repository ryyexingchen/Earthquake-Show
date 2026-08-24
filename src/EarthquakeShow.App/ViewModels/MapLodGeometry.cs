using EarthquakeShow.Core.Models;

namespace EarthquakeShow.App.ViewModels;

public enum MapDetailLevel
{
    Overview,
    Medium,
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

    public MapLodResourceProvider(
        string areasPath,
        string municipalitiesPath,
        string boundariesPath)
    {
        _areasPath = areasPath ?? throw new ArgumentNullException(nameof(areasPath));
        _municipalitiesPath = municipalitiesPath ??
            throw new ArgumentNullException(nameof(municipalitiesPath));
        _boundariesPath = boundariesPath ??
            throw new ArgumentNullException(nameof(boundariesPath));
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
}
