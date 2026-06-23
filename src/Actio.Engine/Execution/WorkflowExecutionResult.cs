using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public sealed record WorkflowExecutionResult
{
    public WorkflowExecutionResult(
        WorkflowExecutionStatus status,
        int successfulSteps,
        int totalSteps,
        IReadOnlyList<string> errors,
        IReadOnlyList<WorkflowRunOutput>? outputs = null,
        IReadOnlyList<WorkflowRunArtifact>? artifacts = null,
        string? runId = null,
        string? runRecordPath = null)
    {
        Status = status;
        SuccessfulSteps = successfulSteps;
        TotalSteps = totalSteps;
        Errors = errors;
        Outputs = outputs ?? [];
        Artifacts = artifacts ?? [];
        RunId = runId;
        RunRecordPath = runRecordPath;
    }

    public WorkflowExecutionStatus Status { get; init; }

    public int SuccessfulSteps { get; init; }

    public int TotalSteps { get; init; }

    public IReadOnlyList<string> Errors { get; init; }

    public IReadOnlyList<WorkflowRunOutput> Outputs { get; init; }

    public IReadOnlyList<WorkflowRunArtifact> Artifacts { get; init; }

    public string? RunId { get; init; }

    public string? RunRecordPath { get; init; }

    public bool Success => Status == WorkflowExecutionStatus.Success;
}
