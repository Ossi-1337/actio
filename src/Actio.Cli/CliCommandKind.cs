namespace Actio.Cli;

public enum CliCommandKind
{
    RunWorkflow,
    ValidateWorkflow,
    RerunWorkflow,
    CancelRun,
    ShowRunStatus,
    RunWeb,
    ListCache,
    CleanCache,
    ShowCompatibility,
    ShowRootHelp,
    ShowRunHelp,
    ShowValidateHelp,
    ShowRerunHelp,
    ShowCancelHelp,
    ShowStatusHelp,
    ShowWebHelp,
    ShowCacheHelp,
    ShowCompatibilityHelp,
    ShowVersion,
    UsageError
}
