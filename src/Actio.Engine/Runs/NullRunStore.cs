using Actio.Core.Workflows;

namespace Actio.Engine.Runs;

public sealed class NullRunStore : IRunStore
{
    public string CreateRunId()
    {
        return DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
    }

    public Task<RunStoragePaths> InitializeRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RunStoragePaths(runId, null, null));
    }

    public Task<string?> WriteStepLogAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        IReadOnlyList<string> outputLines,
        IReadOnlyList<string> errorLines,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<ArtifactSaveResult> SaveArtifactsAsync(
        string runId,
        string jobName,
        string projectRoot,
        IReadOnlyList<WorkflowArtifact> artifacts,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ArtifactSaveResult([], []));
    }

    public Task SaveRunRecordAsync(
        WorkflowRunRecord runRecord,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
