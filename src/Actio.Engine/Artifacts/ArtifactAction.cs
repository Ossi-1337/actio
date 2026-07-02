namespace Actio.Engine.Artifacts;

public sealed record ArtifactAction(
    ArtifactActionKind Kind,
    string? Name,
    IReadOnlyList<string> Paths,
    string DestinationPath,
    int? RetentionDays)
{
    public static ArtifactAction Upload(string name, IReadOnlyList<string> paths, int? retentionDays)
        => new(ArtifactActionKind.Upload, name, paths, ".", retentionDays);

    public static ArtifactAction Download(string? name, string destinationPath)
        => new(ArtifactActionKind.Download, name, [], destinationPath, null);
}
