using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

public sealed record JobRuntimeStartRequest(
    string JobName,
    string ProjectRoot,
    IReadOnlyList<ContainerPortMapping> JobContainerPorts,
    IReadOnlyList<ServiceContainerDefinition> Services);

public sealed record ServiceContainerDefinition(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<ContainerPortMapping>? Ports = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<StepExecutionMount>? Volumes = null)
{
    public IReadOnlyList<ContainerPortMapping> Ports { get; init; } = Ports ?? [];

    public IReadOnlyList<string> Options { get; init; } = Options ?? [];

    public IReadOnlyList<StepExecutionMount> Volumes { get; init; } = Volumes ?? [];
}

public sealed record JobRuntimeStartResult(
    bool Success,
    JobRuntimeContext? Runtime,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];

    public static JobRuntimeStartResult Started(
        JobRuntimeContext runtime,
        IReadOnlyList<string>? warnings = null)
        => new(true, runtime, [], warnings);

    public static JobRuntimeStartResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}

public sealed record JobRuntimeContext(
    string NetworkName,
    IReadOnlyList<string> ServiceContainerNames,
    IReadOnlyList<ContainerPortMapping>? ReservedPorts = null,
    string? PortLeaseOwner = null)
{
    public IReadOnlyList<ContainerPortMapping> ReservedPorts { get; init; } = ReservedPorts ?? [];
}

public sealed record JobRuntimeStopResult(IReadOnlyList<string> Errors);
