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
    IReadOnlyList<WorkflowTrigger>? Triggers = null,
    WorkflowRunTrigger? RunTrigger = null)
{
    public IReadOnlyList<WorkflowTrigger> Triggers { get; init; } = Triggers ?? [];

    public WorkflowRunTrigger RunTrigger { get; init; } = RunTrigger ?? WorkflowRunTrigger.CliWorkflowDispatch;
}

public sealed record WorkflowRunTrigger(
    string EventName,
    string Source,
    IReadOnlyDictionary<string, string>? Inputs = null,
    WorkflowEventPayload? EventPayload = null)
{
    public static WorkflowRunTrigger CliWorkflowDispatch { get; } = new(
        "workflow_dispatch",
        "CLI",
        new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Inputs { get; init; } = Inputs ?? new Dictionary<string, string>();

    public WorkflowEventPayload EventPayload { get; init; } =
        EventPayload ?? WorkflowEventPayload.Create(EventName, Source, inputs: Inputs);
}
