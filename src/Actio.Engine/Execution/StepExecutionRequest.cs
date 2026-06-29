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
    JobServiceNetwork? Services = null)
{
    public IReadOnlyList<StepExecutionMount> AdditionalMounts { get; init; } = AdditionalMounts ?? [];
}

public sealed record JobContainerExecutionOptions(
    string Image,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<StepExecutionMount>? Volumes = null)
{
    public IReadOnlyList<string> Ports { get; init; } = Ports ?? [];

    public IReadOnlyList<string> Options { get; init; } = Options ?? [];

    public IReadOnlyList<StepExecutionMount> Volumes { get; init; } = Volumes ?? [];
}
