namespace Actio.Engine.Caching;

public interface IDependencyCache
{
    Task<DependencyCacheRestoreResult> RestoreAsync(
        DependencyCacheRestoreRequest request,
        CancellationToken cancellationToken = default);

    Task<DependencyCacheSaveResult> SaveAsync(
        DependencyCacheSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DependencyCacheEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<int> CleanAsync(CancellationToken cancellationToken = default);
}
