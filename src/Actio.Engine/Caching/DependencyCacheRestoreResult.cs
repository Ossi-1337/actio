namespace Actio.Engine.Caching;

public sealed record DependencyCacheRestoreResult(
    bool Success,
    bool CacheHit,
    string? MatchedKey,
    string? MatchedRestoreKey,
    DependencyCacheEntry? Entry,
    IReadOnlyList<string> Errors)
{
    public static DependencyCacheRestoreResult Restored(
        DependencyCacheEntry entry,
        bool cacheHit,
        string? matchedRestoreKey)
        => new(true, cacheHit, entry.Key, matchedRestoreKey, entry, []);

    public static DependencyCacheRestoreResult Miss()
        => new(true, false, null, null, null, []);

    public static DependencyCacheRestoreResult Failed(IReadOnlyList<string> errors)
        => new(false, false, null, null, null, errors);
}
