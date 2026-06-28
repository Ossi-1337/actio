namespace Actio.Core.Workflows;

public sealed record WorkflowDocument(
    string Name,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, WorkflowJob> Jobs,
    IReadOnlyList<WorkflowTrigger> Triggers)
{
    public WorkflowDocument(
        string name,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, WorkflowJob> jobs)
        : this(name, env, jobs, [])
    {
    }

    public int StepCount => Jobs.Values.Sum(job => job.Steps.Count);
}

public sealed record WorkflowTrigger(
    string EventName,
    WorkflowTriggerValue? Configuration,
    WorkflowTriggerFilters? Filters = null,
    WorkflowDispatch? Dispatch = null,
    IReadOnlyList<WorkflowSchedule>? Schedules = null,
    IReadOnlyList<string>? ActivityTypes = null)
{
    public WorkflowTriggerFilters Filters { get; init; } = Filters ?? WorkflowTriggerFilters.Empty;

    public WorkflowDispatch Dispatch { get; init; } = Dispatch ?? WorkflowDispatch.Empty;

    public IReadOnlyList<WorkflowSchedule> Schedules { get; init; } = Schedules ?? [];

    public IReadOnlyList<string> ActivityTypes { get; init; } = ActivityTypes ?? [];
}

public sealed record WorkflowDispatch(
    IReadOnlyDictionary<string, WorkflowDispatchInput> Inputs)
{
    public static WorkflowDispatch Empty { get; } = new(new Dictionary<string, WorkflowDispatchInput>());
}

public sealed record WorkflowDispatchInput(
    string Name,
    string? Description,
    bool Required,
    string? Default,
    string Type,
    IReadOnlyList<string> Options);

public sealed record WorkflowSchedule(string Cron);

public sealed record WorkflowTriggerValue(
    string Kind,
    string? Value,
    IReadOnlyList<WorkflowTriggerValue> Items,
    IReadOnlyDictionary<string, WorkflowTriggerValue> Properties)
{
    public static WorkflowTriggerValue Scalar(string value)
        => new("scalar", value, [], new Dictionary<string, WorkflowTriggerValue>());

    public static WorkflowTriggerValue Sequence(IReadOnlyList<WorkflowTriggerValue> items)
        => new("sequence", null, items, new Dictionary<string, WorkflowTriggerValue>());

    public static WorkflowTriggerValue Mapping(IReadOnlyDictionary<string, WorkflowTriggerValue> properties)
        => new("mapping", null, [], properties);
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
