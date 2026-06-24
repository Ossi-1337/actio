namespace Actio.Engine.Actions;

public sealed record DockerImageActionCacheRequest(
    string Uses,
    string Image,
    bool IsPinned,
    string? MutablePart);
