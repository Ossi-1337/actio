namespace Actio.Cli;

public sealed record CliCommand(
    CliCommandKind Kind,
    string? WorkflowName = null,
    string? RunId = null,
    string? ProjectRoot = null,
    string? ActioHome = null,
    string? Url = null,
    bool Background = false,
    IReadOnlyDictionary<string, string>? Inputs = null,
    string? ErrorMessage = null)
{
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = Inputs ?? new Dictionary<string, string>();

    public static CliCommand RunWorkflow(string workflowName)
        => RunWorkflow(workflowName, new Dictionary<string, string>());

    public static CliCommand RunWorkflow(string workflowName, IReadOnlyDictionary<string, string> inputs)
        => new(CliCommandKind.RunWorkflow, WorkflowName: workflowName, Inputs: inputs);

    public static CliCommand RerunWorkflow(string runId)
        => new(CliCommandKind.RerunWorkflow, RunId: runId);

    public static CliCommand CancelRun(string runId)
        => new(CliCommandKind.CancelRun, RunId: runId);

    public static CliCommand ShowRunStatus(string runId)
        => new(CliCommandKind.ShowRunStatus, RunId: runId);

    public static CliCommand RunWeb(string? projectRoot, string? actioHome, string? url, bool background)
        => new(CliCommandKind.RunWeb, ProjectRoot: projectRoot, ActioHome: actioHome, Url: url, Background: background);

    public static CliCommand ListCache()
        => new(CliCommandKind.ListCache);

    public static CliCommand CleanCache()
        => new(CliCommandKind.CleanCache);

    public static CliCommand UsageError(string message)
        => new(CliCommandKind.UsageError, ErrorMessage: message);
}
