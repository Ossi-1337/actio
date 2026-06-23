namespace Actio.Web.Models;

public sealed record ArtifactResult(
    string Path,
    bool IsFile,
    string? ContentType,
    IReadOnlyList<string> DirectoryEntries);
