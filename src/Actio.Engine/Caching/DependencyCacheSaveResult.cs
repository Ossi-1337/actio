namespace Actio.Engine.Caching;

public sealed record DependencyCacheSaveResult(
    bool Success,
    bool Saved,
    DependencyCacheEntry? Entry,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Errors)
{
    public static DependencyCacheSaveResult SavedEntry(DependencyCacheEntry entry, IReadOnlyList<string>? messages = null)
        => new(true, true, entry, messages ?? [], []);

    public static DependencyCacheSaveResult Skipped(IReadOnlyList<string> messages, DependencyCacheEntry? entry = null)
        => new(true, false, entry, messages, []);

    public static DependencyCacheSaveResult Failed(IReadOnlyList<string> errors)
        => new(false, false, null, [], errors);
}
