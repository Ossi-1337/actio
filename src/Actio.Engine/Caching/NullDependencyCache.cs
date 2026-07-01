namespace Actio.Engine.Caching;

public sealed class NullDependencyCache : IDependencyCache
{
    public static NullDependencyCache Instance { get; } = new();

    private NullDependencyCache()
    {
    }

    public Task<DependencyCacheRestoreResult> RestoreAsync(
        DependencyCacheRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DependencyCacheRestoreResult.Miss());
    }

    public Task<DependencyCacheSaveResult> SaveAsync(
        DependencyCacheSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DependencyCacheSaveResult.Skipped(["No dependency cache store is configured."]));
    }

    public Task<IReadOnlyList<DependencyCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<DependencyCacheEntry>>([]);
    }

    public Task<int> CleanAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }
}
