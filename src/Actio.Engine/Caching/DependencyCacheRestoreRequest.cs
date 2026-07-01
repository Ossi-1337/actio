namespace Actio.Engine.Caching;

public sealed record DependencyCacheRestoreRequest(
    string ProjectRoot,
    string Key,
    IReadOnlyList<string> RestoreKeys,
    IReadOnlyList<string> Paths);
