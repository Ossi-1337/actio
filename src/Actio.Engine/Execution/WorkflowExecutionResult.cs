namespace Actio.Engine.Execution;

public sealed record WorkflowExecutionResult(
    WorkflowExecutionStatus Status,
    int SuccessfulSteps,
    int TotalSteps,
    IReadOnlyList<string> Errors)
{
    public bool Success => Status == WorkflowExecutionStatus.Success;
}
