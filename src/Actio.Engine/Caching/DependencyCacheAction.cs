namespace Actio.Engine.Caching;

public sealed record DependencyCacheAction(
    string Key,
    IReadOnlyList<string> RestoreKeys,
    IReadOnlyList<string> Paths);
