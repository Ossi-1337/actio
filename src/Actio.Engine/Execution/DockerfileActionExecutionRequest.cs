namespace Actio.Engine.Execution;

public sealed record DockerfileActionExecutionRequest(
    string JobName,
    string StepName,
    string Image,
    string ProjectRoot,
    string BuildContext,
    string DockerfilePath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<StepExecutionMount>? AdditionalMounts = null,
    JobRuntimeContext? Runtime = null,
    string? EntryPoint = null,
    IReadOnlyList<string>? Arguments = null,
    string? BuildContextStagingRoot = null)
{
    public IReadOnlyList<StepExecutionMount> AdditionalMounts { get; init; } = AdditionalMounts ?? [];

    public IReadOnlyList<string> Arguments { get; init; } = Arguments ?? [];
}
