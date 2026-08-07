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
    InstallHooks,
    ShowHooksStatus,
    UninstallHooks,
    RunPrePushHook,
    ShowRootHelp,
    ShowRunHelp,
    ShowValidateHelp,
    ShowRerunHelp,
    ShowCancelHelp,
    ShowStatusHelp,
    ShowWebHelp,
    ShowCacheHelp,
    ShowCompatibilityHelp,
    ShowHooksHelp,
    ShowVersion,
    UsageError
}
