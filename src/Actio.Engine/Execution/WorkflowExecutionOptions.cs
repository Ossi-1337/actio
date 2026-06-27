using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public sealed record WorkflowExecutionOptions(
    string ProjectRoot,
    string? WorkflowPath = null,
    string? RunId = null,
    WorkflowRunTrigger? RunTrigger = null)
{
    public WorkflowRunTrigger RunTrigger { get; init; } = RunTrigger ?? WorkflowRunTrigger.CliWorkflowDispatch;
}
