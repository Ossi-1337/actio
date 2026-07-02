namespace Actio.Engine.Runs;

public sealed record WorkflowRunArtifact(
    string JobName,
    string Name,
    string SourcePath,
    string StoredPath,
    int? RetentionDays = null);
