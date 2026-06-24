namespace Actio.Core.Workflows;

public sealed record WorkflowParseResult(
    bool Success,
    WorkflowDocument? Workflow,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static WorkflowParseResult Parsed(
        WorkflowDocument workflow,
        IReadOnlyList<string>? warnings = null)
    {
        return new WorkflowParseResult(true, workflow, [], warnings ?? []);
    }

    public static WorkflowParseResult Failed(
        IReadOnlyList<string> errors,
        IReadOnlyList<string>? warnings = null)
    {
        return new WorkflowParseResult(false, null, errors, warnings ?? []);
    }
}
