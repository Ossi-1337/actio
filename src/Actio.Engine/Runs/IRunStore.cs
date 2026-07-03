using Actio.Core.Workflows;

namespace Actio.Engine.Runs;

public interface IRunStore
{
    string CreateRunId();

    Task<RunStoragePaths> InitializeRunAsync(string runId, CancellationToken cancellationToken = default);

    Task<IStepLog> OpenStepLogAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        CancellationToken cancellationToken = default);

    Task<StepEnvironmentFiles> CreateStepEnvironmentFilesAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        CancellationToken cancellationToken = default);

    Task<ArtifactSaveResult> SaveArtifactsAsync(
        string runId,
        string jobName,
        string projectRoot,
        IReadOnlyList<WorkflowArtifact> artifacts,
        CancellationToken cancellationToken = default);

    Task<ArtifactSaveResult> SaveArtifactAsync(
        string runId,
        string jobName,
        string projectRoot,
        string artifactName,
        IReadOnlyList<string> paths,
        int? retentionDays = null,
        CancellationToken cancellationToken = default);

    Task<ArtifactDownloadResult> RestoreArtifactsAsync(
        string projectRoot,
        IReadOnlyList<WorkflowRunArtifact> artifacts,
        string destinationPath,
        bool useArtifactNameSubdirectories,
        CancellationToken cancellationToken = default);

    Task RequestRunCancellationAsync(string runId, CancellationToken cancellationToken = default);

    Task<bool> IsRunCancellationRequestedAsync(string runId, CancellationToken cancellationToken = default);

    Task SaveRunRecordAsync(WorkflowRunRecord runRecord, CancellationToken cancellationToken = default);
}
