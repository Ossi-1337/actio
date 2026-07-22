using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

public sealed record StepExecutionRequest(
    string JobName,
    string StepName,
    string RunsOn,
    string Command,
    string ProjectRoot,
    IReadOnlyDictionary<string, string> Environment,
    string? Shell = null,
    string? WorkingDirectory = null,
    IReadOnlyList<StepExecutionMount>? AdditionalMounts = null,
    JobContainerExecutionOptions? Container = null,
    JobRuntimeContext? Runtime = null)
{
    public IReadOnlyList<StepExecutionMount> AdditionalMounts { get; init; } = AdditionalMounts ?? [];
}

public sealed record JobContainerExecutionOptions(
    string Image,
    IReadOnlyList<ContainerPortMapping>? Ports = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<StepExecutionMount>? Volumes = null)
{
    public IReadOnlyList<ContainerPortMapping> Ports { get; init; } = Ports ?? [];

    public IReadOnlyList<string> Options { get; init; } = Options ?? [];

    public IReadOnlyList<StepExecutionMount> Volumes { get; init; } = Volumes ?? [];
}
