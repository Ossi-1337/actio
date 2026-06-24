namespace Actio.Engine.Actions;

public interface IActionCache
{
    Task<ActionCacheEntry> GetOrAddLocalActionAsync(
        LocalActionCacheRequest request,
        CancellationToken cancellationToken = default);

    Task<ActionCacheEntry> GetOrAddDockerImageActionAsync(
        DockerImageActionCacheRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionCacheEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<int> CleanAsync(CancellationToken cancellationToken = default);
}
