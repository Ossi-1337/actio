namespace Actio.Engine.Execution;

public sealed record JavaScriptActionExecutionRequest(
    string JobName,
    string StepName,
    string ProjectRoot,
    string ActionPath,
    string Main,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<StepExecutionMount>? AdditionalMounts = null,
    JobRuntimeContext? Runtime = null,
    string? Pre = null,
    string? Post = null)
{
    public IReadOnlyList<StepExecutionMount> AdditionalMounts { get; init; } = AdditionalMounts ?? [];
}
