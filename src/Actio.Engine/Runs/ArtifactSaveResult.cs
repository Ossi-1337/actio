namespace Actio.Engine.Runs;

public sealed record ArtifactSaveResult(
    IReadOnlyList<WorkflowRunArtifact> Artifacts,
    IReadOnlyList<string> Errors);
