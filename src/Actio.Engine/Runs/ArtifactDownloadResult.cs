namespace Actio.Engine.Runs;

public sealed record ArtifactDownloadResult(
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> Errors);
