namespace Actio.Core.Workflows;

public sealed record WorkflowDocument(
    string Name,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, WorkflowJob> Jobs)
{
    public int StepCount => Jobs.Values.Sum(job => job.Steps.Count);
}

public sealed record WorkflowJob
{
    public WorkflowJob(
        string name,
        IReadOnlyList<string> needs,
        string? ifExpression,
        string runsOn,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<WorkflowStep> steps)
        : this(name, needs, ifExpression, runsOn, outputs, [], steps)
    {
    }

    public WorkflowJob(
        string name,
        IReadOnlyList<string> needs,
        string? ifExpression,
        string runsOn,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<WorkflowArtifact> artifacts,
        IReadOnlyList<WorkflowStep> steps)
    {
        Name = name;
        Needs = needs;
        If = ifExpression;
        RunsOn = runsOn;
        Outputs = outputs;
        Artifacts = artifacts;
        Steps = steps;
    }

    public string Name { get; init; }

    public IReadOnlyList<string> Needs { get; init; }

    public string? If { get; init; }

    public string RunsOn { get; init; }

    public IReadOnlyDictionary<string, string> Outputs { get; init; }

    public IReadOnlyList<WorkflowArtifact> Artifacts { get; init; }

    public IReadOnlyList<WorkflowStep> Steps { get; init; }
}

public sealed record WorkflowArtifact(
    string Name,
    string Path);

public sealed record WorkflowStep(
    string Name,
    string? Run,
    string? Uses);
