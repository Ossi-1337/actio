using Actio.Engine.Execution;

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
    string SecurityProfile = RunnerSecurityProfiles.SecureBaseline,
    string? RemoteName = null,
    string? RemoteUrl = null,
    string? ErrorMessage = null)
{
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = Inputs ?? new Dictionary<string, string>();

    public static CliCommand RunWorkflow(string workflowName)
        => RunWorkflow(workflowName, new Dictionary<string, string>());

    public static CliCommand RunWorkflow(
        string workflowName,
        IReadOnlyDictionary<string, string> inputs,
        string securityProfile = RunnerSecurityProfiles.SecureBaseline)
        => new(
            CliCommandKind.RunWorkflow,
            WorkflowName: workflowName,
            Inputs: inputs,
            SecurityProfile: securityProfile);

    public static CliCommand ValidateWorkflow(
        string workflowName,
        IReadOnlyDictionary<string, string> inputs)
        => new(
            CliCommandKind.ValidateWorkflow,
            WorkflowName: workflowName,
            Inputs: inputs);

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

    public static CliCommand ShowCompatibility()
        => new(CliCommandKind.ShowCompatibility);

    public static CliCommand InstallHooks()
        => new(CliCommandKind.InstallHooks);

    public static CliCommand ShowHooksStatus()
        => new(CliCommandKind.ShowHooksStatus);

    public static CliCommand UninstallHooks()
        => new(CliCommandKind.UninstallHooks);

    public static CliCommand RunPrePushHook(string remoteName, string remoteUrl)
        => new(CliCommandKind.RunPrePushHook, RemoteName: remoteName, RemoteUrl: remoteUrl);

    public static CliCommand UsageError(string message)
        => new(CliCommandKind.UsageError, ErrorMessage: message);
}
