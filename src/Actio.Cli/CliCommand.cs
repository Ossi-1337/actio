namespace Actio.Cli;

public sealed record CliCommand(
    CliCommandKind Kind,
    string? WorkflowName = null,
    string? ProjectRoot = null,
    string? ActioHome = null,
    string? Url = null,
    string? ErrorMessage = null)
{
    public static CliCommand RunWorkflow(string workflowName)
        => new(CliCommandKind.RunWorkflow, WorkflowName: workflowName);

    public static CliCommand RunWeb(string? projectRoot, string? actioHome, string? url)
        => new(CliCommandKind.RunWeb, ProjectRoot: projectRoot, ActioHome: actioHome, Url: url);

    public static CliCommand UsageError(string message)
        => new(CliCommandKind.UsageError, ErrorMessage: message);
}
