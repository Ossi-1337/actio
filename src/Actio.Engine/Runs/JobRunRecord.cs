namespace Actio.Engine.Runs;

public sealed record JobRunRecord(
    string Name,
    string Status,
    string RunsOn,
    IReadOnlyList<string> Needs,
    string? If,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMilliseconds,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<StepRunRecord> Steps,
    IReadOnlyList<WorkflowRunArtifact> Artifacts,
    IReadOnlyList<string> Errors);
