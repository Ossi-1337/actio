namespace Actio.Engine.Execution;

public sealed record DockerActionExecutionRequest(
    string JobName,
    string StepName,
    string Image,
    string ProjectRoot,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<StepExecutionMount>? AdditionalMounts = null)
{
    public IReadOnlyList<StepExecutionMount> AdditionalMounts { get; init; } = AdditionalMounts ?? [];
}
