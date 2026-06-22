namespace Actio.Cli;

public sealed record WorkflowResolutionResult(
    bool Success,
    string? ProjectRoot,
    string? WorkflowPath,
    IReadOnlyList<string> Errors)
{
    public static WorkflowResolutionResult Resolved(string projectRoot, string workflowPath)
        => new(true, projectRoot, workflowPath, []);

    public static WorkflowResolutionResult Failed(IReadOnlyList<string> errors)
        => new(false, null, null, errors);
}
