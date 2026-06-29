namespace Actio.Core.Workflows;

public sealed record WorkflowDocument(
    string Name,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, WorkflowJob> Jobs,
    IReadOnlyList<WorkflowTrigger> Triggers,
    WorkflowRunDefaults? Defaults = null)
{
    public WorkflowDocument(
        string name,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, WorkflowJob> jobs)
        : this(name, env, jobs, [], WorkflowRunDefaults.Empty)
    {
    }

    public WorkflowRunDefaults Defaults { get; init; } = Defaults ?? WorkflowRunDefaults.Empty;

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

public sealed record WorkflowRunDefaults(
    string? Shell,
    string? WorkingDirectory)
{
    public static WorkflowRunDefaults Empty { get; } = new(null, null);

    public WorkflowRunDefaults Merge(WorkflowRunDefaults other)
    {
        return new WorkflowRunDefaults(
            other.Shell ?? Shell,
            other.WorkingDirectory ?? WorkingDirectory);
    }
}

public sealed record WorkflowJobConcurrency(
    string Group,
    bool CancelInProgress);

public sealed record WorkflowJobStrategy(
    WorkflowJobMatrix Matrix,
    bool FailFast = true,
    int? MaxParallel = null)
{
    public static WorkflowJobStrategy Empty { get; } = new(WorkflowJobMatrix.Empty);
}

public sealed record WorkflowJobMatrix(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Axes,
    IReadOnlyList<IReadOnlyDictionary<string, string>>? Include = null,
    IReadOnlyList<IReadOnlyDictionary<string, string>>? Exclude = null)
{
    public static WorkflowJobMatrix Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Include { get; init; } = Include ?? [];

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Exclude { get; init; } = Exclude ?? [];
}

public sealed record WorkflowJobContainer(
    string Image,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyList<WorkflowJobContainerVolume>? Volumes = null,
    IReadOnlyList<string>? Options = null)
{
    public IReadOnlyDictionary<string, string> Env { get; init; } = Env ?? new Dictionary<string, string>();

    public IReadOnlyList<string> Ports { get; init; } = Ports ?? [];

    public IReadOnlyList<WorkflowJobContainerVolume> Volumes { get; init; } = Volumes ?? [];

    public IReadOnlyList<string> Options { get; init; } = Options ?? [];
}

public sealed record WorkflowJobContainerVolume(
    string Source,
    string Target,
    bool ReadOnly);

public sealed record WorkflowJobService(
    string Image,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyList<WorkflowJobContainerVolume>? Volumes = null,
    IReadOnlyList<string>? Options = null)
{
    public IReadOnlyDictionary<string, string> Env { get; init; } = Env ?? new Dictionary<string, string>();

    public IReadOnlyList<string> Ports { get; init; } = Ports ?? [];

    public IReadOnlyList<WorkflowJobContainerVolume> Volumes { get; init; } = Volumes ?? [];

    public IReadOnlyList<string> Options { get; init; } = Options ?? [];
}

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
        : this(
            name,
            null,
            needs,
            ifExpression,
            runsOn,
            new Dictionary<string, string>(),
            WorkflowRunDefaults.Empty,
            null,
            false,
            null,
            WorkflowJobStrategy.Empty,
            outputs,
            artifacts,
            steps)
    {
    }

    public WorkflowJob(
        string name,
        string? displayName,
        IReadOnlyList<string> needs,
        string? ifExpression,
        string runsOn,
        IReadOnlyDictionary<string, string> env,
        WorkflowRunDefaults? defaults,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<WorkflowArtifact> artifacts,
        IReadOnlyList<WorkflowStep> steps)
        : this(
            name,
            displayName,
            needs,
            ifExpression,
            runsOn,
            env,
            defaults,
            null,
            false,
            null,
            WorkflowJobStrategy.Empty,
            outputs,
            artifacts,
            steps)
    {
    }

    public WorkflowJob(
        string name,
        string? displayName,
        IReadOnlyList<string> needs,
        string? ifExpression,
        string runsOn,
        IReadOnlyDictionary<string, string> env,
        WorkflowRunDefaults? defaults,
        int? timeoutMinutes,
        bool continueOnError,
        WorkflowJobConcurrency? concurrency,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<WorkflowArtifact> artifacts,
        IReadOnlyList<WorkflowStep> steps)
        : this(
            name,
            displayName,
            needs,
            ifExpression,
            runsOn,
            env,
            defaults,
            timeoutMinutes,
            continueOnError,
            concurrency,
            WorkflowJobStrategy.Empty,
            outputs,
            artifacts,
            steps)
    {
    }

    public WorkflowJob(
        string name,
        string? displayName,
        IReadOnlyList<string> needs,
        string? ifExpression,
        string runsOn,
        IReadOnlyDictionary<string, string> env,
        WorkflowRunDefaults? defaults,
        int? timeoutMinutes,
        bool continueOnError,
        WorkflowJobConcurrency? concurrency,
        WorkflowJobStrategy? strategy,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<WorkflowArtifact> artifacts,
        IReadOnlyList<WorkflowStep> steps,
        WorkflowJobContainer? container = null,
        IReadOnlyDictionary<string, WorkflowJobService>? services = null)
    {
        Name = name;
        BaseName = name;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
        Needs = needs;
        LogicalNeeds = needs;
        If = ifExpression;
        RunsOn = runsOn;
        Env = env;
        Defaults = defaults ?? WorkflowRunDefaults.Empty;
        TimeoutMinutes = timeoutMinutes;
        ContinueOnError = continueOnError;
        Concurrency = concurrency;
        Strategy = strategy ?? WorkflowJobStrategy.Empty;
        Container = container;
        Services = services ?? new Dictionary<string, WorkflowJobService>();
        Matrix = new Dictionary<string, string>();
        Outputs = outputs;
        Artifacts = artifacts;
        Steps = steps;
    }

    public string Name { get; init; }

    public string BaseName { get; init; }

    public string DisplayName { get; init; }

    public IReadOnlyList<string> Needs { get; init; }

    public IReadOnlyList<string> LogicalNeeds { get; init; }

    public string? If { get; init; }

    public string RunsOn { get; init; }

    public IReadOnlyDictionary<string, string> Env { get; init; }

    public WorkflowRunDefaults Defaults { get; init; }

    public int? TimeoutMinutes { get; init; }

    public bool ContinueOnError { get; init; }

    public WorkflowJobConcurrency? Concurrency { get; init; }

    public WorkflowJobStrategy Strategy { get; init; }

    public WorkflowJobContainer? Container { get; init; }

    public IReadOnlyDictionary<string, WorkflowJobService> Services { get; init; }

    public IReadOnlyDictionary<string, string> Matrix { get; init; }

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
    string? Uses,
    string? Id = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Shell = null,
    string? WorkingDirectory = null,
    string? If = null,
    int? TimeoutMinutes = null,
    bool ContinueOnError = false,
    IReadOnlyDictionary<string, string>? With = null)
{
    public IReadOnlyDictionary<string, string> Env { get; init; } = Env ?? new Dictionary<string, string>();

    public string? If { get; init; } = If;

    public int? TimeoutMinutes { get; init; } = TimeoutMinutes;

    public bool ContinueOnError { get; init; } = ContinueOnError;

    public IReadOnlyDictionary<string, string> With { get; init; } = With ?? new Dictionary<string, string>();
}
