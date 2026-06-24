namespace Actio.Engine.Actions;

public sealed record LocalActionCacheRequest(
    string Uses,
    string SourcePath,
    string ContentHash);
