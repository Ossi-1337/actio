namespace Actio.Engine.Actions;

public sealed class NullActionCache : IActionCache, IGitHubActionSourceProvider
{
    public static NullActionCache Instance { get; } = new();

    private NullActionCache()
    {
    }

    public Task<ActionCacheEntry> GetOrAddLocalActionAsync(
        LocalActionCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        var entry = new ActionCacheEntry(
            request.ContentHash,
            "local",
            request.Uses,
            request.SourcePath,
            request.ContentHash,
            string.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        return Task.FromResult(entry);
    }

    public Task<ActionCacheEntry> GetOrAddDockerImageActionAsync(
        DockerImageActionCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new ActionCacheEntry(
            request.Image,
            "docker",
            request.Uses,
            request.Image,
            request.Image,
            string.Empty,
            now,
            now,
            request.IsPinned ? request.Image : null,
            request.IsPinned ? null : request.MutablePart);

        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ActionCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ActionCacheEntry>>([]);
    }

    public Task<GitHubActionSourceResult> GetGitHubActionSourceAsync(
        GitHubActionSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GitHubActionSourceResult.Failed(
            [$"uses '{request.Uses}' is a recognized GitHub action reference, but no GitHub action source provider is configured."]));
    }

    public Task<int> CleanAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }
}
