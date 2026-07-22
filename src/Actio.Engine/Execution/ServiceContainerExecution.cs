namespace Actio.Engine.Execution;

public sealed record ServiceContainerStartRequest(
    string JobName,
    string ProjectRoot,
    IReadOnlyList<ServiceContainerDefinition> Services);

public sealed record ServiceContainerDefinition(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<StepExecutionMount>? Volumes = null)
{
    public IReadOnlyList<string> Ports { get; init; } = Ports ?? [];

    public IReadOnlyList<string> Options { get; init; } = Options ?? [];

    public IReadOnlyList<StepExecutionMount> Volumes { get; init; } = Volumes ?? [];
}

public sealed record ServiceContainerStartResult(
    bool Success,
    JobServiceNetwork? Network,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];

    public static ServiceContainerStartResult Started(
        JobServiceNetwork? network,
        IReadOnlyList<string>? warnings = null)
        => new(true, network, [], warnings);

    public static ServiceContainerStartResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}

public sealed record JobServiceNetwork(
    string NetworkName,
    IReadOnlyList<string> ContainerNames);

public sealed record ServiceContainerStopResult(IReadOnlyList<string> Errors);
