namespace Actio.Engine.Caching;

public sealed record DependencyCacheEntry(
    string Key,
    string Version,
    IReadOnlyList<string> Paths,
    string CachePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);
