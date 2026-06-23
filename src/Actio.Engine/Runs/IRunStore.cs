using Actio.Core.Workflows;

namespace Actio.Engine.Runs;

public interface IRunStore
{
    string CreateRunId();

    Task<RunStoragePaths> InitializeRunAsync(string runId, CancellationToken cancellationToken = default);

    Task<string?> WriteStepLogAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        IReadOnlyList<string> outputLines,
        IReadOnlyList<string> errorLines,
        CancellationToken cancellationToken = default);

    Task<ArtifactSaveResult> SaveArtifactsAsync(
        string runId,
        string jobName,
        string projectRoot,
        IReadOnlyList<WorkflowArtifact> artifacts,
        CancellationToken cancellationToken = default);

    Task SaveRunRecordAsync(WorkflowRunRecord runRecord, CancellationToken cancellationToken = default);
}
