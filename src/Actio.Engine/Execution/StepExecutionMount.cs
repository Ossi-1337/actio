namespace Actio.Engine.Execution;

public sealed record StepExecutionMount(
    string HostPath,
    string ContainerPath,
    bool ReadOnly,
    StepExecutionMountKind Kind = StepExecutionMountKind.Workflow);

public enum StepExecutionMountKind
{
    Workflow,
    ActionSource,
    EnvironmentFiles,
    WorkspaceMask
}
