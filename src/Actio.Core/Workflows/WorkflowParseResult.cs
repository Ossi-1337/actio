namespace Actio.Core.Workflows;

public sealed record WorkflowParseResult(
    bool Success,
    WorkflowDocument? Workflow,
    IReadOnlyList<string> Errors)
{
    public static WorkflowParseResult Parsed(WorkflowDocument workflow)
        => new(true, workflow, []);

    public static WorkflowParseResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}
