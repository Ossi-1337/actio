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
    ShowRootHelp,
    ShowRunHelp,
    ShowRerunHelp,
    ShowCancelHelp,
    ShowStatusHelp,
    ShowWebHelp,
    ShowCacheHelp,
    ShowVersion,
    UsageError
}
