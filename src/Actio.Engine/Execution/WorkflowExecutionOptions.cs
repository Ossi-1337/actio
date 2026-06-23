namespace Actio.Engine.Execution;

public sealed record WorkflowExecutionOptions(
    string ProjectRoot,
    string? WorkflowPath = null,
    string? RunId = null);
