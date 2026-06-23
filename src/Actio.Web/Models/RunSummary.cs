namespace Actio.Web.Models;

public sealed record RunSummary(
    string RunId,
    string WorkflowName,
    string? WorkflowPath,
    string Status,
    DateTimeOffset StartedAt,
    long DurationMilliseconds,
    string Trigger,
    int JobCount,
    int ArtifactCount);
