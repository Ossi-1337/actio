namespace Actio.Cli;

public sealed record CliCommand(
    CliCommandKind Kind,
    string? WorkflowName = null,
    string? ErrorMessage = null)
{
    public static CliCommand RunWorkflow(string workflowName)
        => new(CliCommandKind.RunWorkflow, WorkflowName: workflowName);

    public static CliCommand UsageError(string message)
        => new(CliCommandKind.UsageError, ErrorMessage: message);
}
