namespace Actio.Engine.Actions;

public sealed record GitHubActionSourceResult(
    bool Success,
    string? ActionFilePath,
    string? ActionDirectory,
    ActionCacheEntry? CacheEntry,
    IReadOnlyList<string> Errors)
{
    public static GitHubActionSourceResult Resolved(
        string actionFilePath,
        string actionDirectory,
        ActionCacheEntry cacheEntry)
    {
        return new GitHubActionSourceResult(true, actionFilePath, actionDirectory, cacheEntry, []);
    }

    public static GitHubActionSourceResult Failed(IReadOnlyList<string> errors)
    {
        return new GitHubActionSourceResult(false, null, null, null, errors);
    }
}
