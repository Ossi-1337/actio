namespace Actio.Cli;

public sealed record CliCommand(
    CliCommandKind Kind,
    string? WorkflowName = null,
    string? ProjectRoot = null,
    string? ActioHome = null,
    string? Url = null,
    bool Background = false,
    string? ErrorMessage = null)
{
    public static CliCommand RunWorkflow(string workflowName)
        => new(CliCommandKind.RunWorkflow, WorkflowName: workflowName);

    public static CliCommand RunWeb(string? projectRoot, string? actioHome, string? url, bool background)
        => new(CliCommandKind.RunWeb, ProjectRoot: projectRoot, ActioHome: actioHome, Url: url, Background: background);

    public static CliCommand ListCache()
        => new(CliCommandKind.ListCache);

    public static CliCommand CleanCache()
        => new(CliCommandKind.CleanCache);

    public static CliCommand UsageError(string message)
        => new(CliCommandKind.UsageError, ErrorMessage: message);
}
