using Actio.Core.Workflows;

namespace Actio.Engine.Runs;

public sealed record JobRunRecord(
    string Name,
    string Status,
    string RunsOn,
    IReadOnlyList<string> Needs,
    string? If,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMilliseconds,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<StepRunRecord> Steps,
    IReadOnlyList<WorkflowRunArtifact> Artifacts,
    IReadOnlyList<string> Errors,
    string? Id = null,
    int? TimeoutMinutes = null,
    bool ContinueOnError = false,
    string? ConcurrencyGroup = null,
    bool ConcurrencyCancelInProgress = false,
    IReadOnlyDictionary<string, string>? Matrix = null,
    WorkflowJobEnvironment? Environment = null)
{
    public string Id { get; init; } = Id ?? Name;

    public int? TimeoutMinutes { get; init; } = TimeoutMinutes;

    public bool ContinueOnError { get; init; } = ContinueOnError;

    public string? ConcurrencyGroup { get; init; } = ConcurrencyGroup;

    public bool ConcurrencyCancelInProgress { get; init; } = ConcurrencyCancelInProgress;

    public IReadOnlyDictionary<string, string> Matrix { get; init; } = Matrix ?? new Dictionary<string, string>();

    public WorkflowJobEnvironment? Environment { get; init; } = Environment;
}
