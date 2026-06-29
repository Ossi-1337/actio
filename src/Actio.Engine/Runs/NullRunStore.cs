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

    public Task<IStepLog> OpenStepLogAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IStepLog>(NullStepLog.Instance);
    }

    public Task<StepEnvironmentFiles> CreateStepEnvironmentFilesAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "actio-env-files",
            SanitizePathSegment(runId),
            SanitizePathSegment(jobName),
            $"{stepIndex + 1:D3}-{SanitizePathSegment(stepName)}");

        Directory.CreateDirectory(directory);
        return Task.FromResult(CreateEnvironmentFiles(directory));
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

    private static StepEnvironmentFiles CreateEnvironmentFiles(string directory)
    {
        var files = new StepEnvironmentFiles(
            directory,
            Path.Combine(directory, StepEnvironmentFiles.EnvironmentFileName),
            Path.Combine(directory, StepEnvironmentFiles.OutputFileName),
            Path.Combine(directory, StepEnvironmentFiles.PathFileName),
            Path.Combine(directory, StepEnvironmentFiles.StepSummaryFileName),
            Path.Combine(directory, StepEnvironmentFiles.StateFileName));

        File.WriteAllText(files.EnvironmentFilePath, string.Empty);
        File.WriteAllText(files.OutputFilePath, string.Empty);
        File.WriteAllText(files.PathFilePath, string.Empty);
        File.WriteAllText(files.StepSummaryFilePath, string.Empty);
        File.WriteAllText(files.StateFilePath, string.Empty);
        return files;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidChars.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }
}
