namespace Actio.Engine.Execution;

public sealed record StepExecutionMount(
    string HostPath,
    string ContainerPath,
    bool ReadOnly);
