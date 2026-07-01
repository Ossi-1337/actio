namespace Actio.Engine.Caching;

public sealed record DependencyCacheSaveRequest(
    string ProjectRoot,
    string Key,
    IReadOnlyList<string> Paths);
