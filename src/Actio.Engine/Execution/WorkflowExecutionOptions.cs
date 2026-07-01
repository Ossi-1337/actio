using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public sealed record WorkflowExecutionOptions(
    string ProjectRoot,
    string? WorkflowPath = null,
    string? RunId = null,
    WorkflowRunTrigger? RunTrigger = null,
    IReadOnlyList<string>? ReusableWorkflowCallStack = null,
    IReadOnlyDictionary<string, string>? Secrets = null)
{
    public WorkflowRunTrigger RunTrigger { get; init; } = RunTrigger ?? WorkflowRunTrigger.CliWorkflowDispatch;

    public IReadOnlyList<string> ReusableWorkflowCallStack { get; init; } = ReusableWorkflowCallStack ?? [];

    public IReadOnlyDictionary<string, string> Secrets { get; init; } = Secrets ?? new Dictionary<string, string>();
}
