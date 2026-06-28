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
    IReadOnlyList<StepExecutionMount>? AdditionalMounts = null)
{
    public IReadOnlyList<StepExecutionMount> AdditionalMounts { get; init; } = AdditionalMounts ?? [];
}
