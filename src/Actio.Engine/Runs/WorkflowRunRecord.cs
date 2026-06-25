using Actio.Core.Workflows;

namespace Actio.Engine.Runs;

public sealed record WorkflowRunRecord(
    string RunId,
    string WorkflowName,
    string? WorkflowPath,
    string ProjectRoot,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long DurationMilliseconds,
    IReadOnlyList<JobRunRecord> Jobs,
    IReadOnlyList<WorkflowRunOutput> Outputs,
    IReadOnlyList<WorkflowRunArtifact> Artifacts,
    IReadOnlyList<string> Errors,
    IReadOnlyList<WorkflowTrigger>? Triggers = null)
{
    public IReadOnlyList<WorkflowTrigger> Triggers { get; init; } = Triggers ?? [];
}
