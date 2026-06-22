namespace Actio.Engine.Execution;

public sealed record StepExecutionRequest(
    string JobName,
    string StepName,
    string RunsOn,
    string Command,
    string ProjectRoot,
    IReadOnlyDictionary<string, string> Environment);
