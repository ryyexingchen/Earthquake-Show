using EarthquakeShow.Core.Services;

namespace EarthquakeShow.Core.Abstractions;

/// <summary>
/// 海啸观测点及近海发布 code 目录的持久化边界。
/// </summary>
public interface ITsunamiStationCatalogRepository
{
    Task<JmaTsunamiStationCatalog> LoadStationCatalogAsync(
        CancellationToken cancellationToken = default);

    Task SaveStationCatalogAsync(
        JmaTsunamiStationCatalog catalog,
        CancellationToken cancellationToken = default);
}
