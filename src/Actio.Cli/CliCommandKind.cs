namespace Actio.Cli;

public enum CliCommandKind
{
    RunWorkflow,
    RerunWorkflow,
    CancelRun,
    ShowRunStatus,
    RunWeb,
    ListCache,
    CleanCache,
    ShowCompatibility,
    ShowRootHelp,
    ShowRunHelp,
    ShowRerunHelp,
    ShowCancelHelp,
    ShowStatusHelp,
    ShowWebHelp,
    ShowCacheHelp,
    ShowCompatibilityHelp,
    ShowVersion,
    UsageError
}
