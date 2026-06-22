namespace Actio.Cli;

public sealed record WorkflowDocument(
    string Name,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, WorkflowJob> Jobs)
{
    public int StepCount => Jobs.Values.Sum(job => job.Steps.Count);
}

public sealed record WorkflowJob(
    string Name,
    IReadOnlyList<string> Needs,
    string? If,
    string RunsOn,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<WorkflowStep> Steps);

public sealed record WorkflowStep(
    string Name,
    string? Run,
    string? Uses);
