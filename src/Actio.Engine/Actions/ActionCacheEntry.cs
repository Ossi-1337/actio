namespace Actio.Engine.Actions;

public sealed record ActionCacheEntry(
    string Key,
    string Kind,
    string Uses,
    string SourcePath,
    string ContentHash,
    string CachePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);
