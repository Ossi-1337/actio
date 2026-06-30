namespace Actio.Engine.Actions;

public sealed record DockerfileActionCacheRequest(
    string Uses,
    string ActionDirectory,
    string DockerfilePath,
    string ContentHash,
    string? PinnedIdentity = null,
    string? MutablePart = null);
