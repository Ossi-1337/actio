using Actio.Core.Workflows;

namespace Actio.Engine.Runs;

public sealed class NullRunStore : IRunStore
{
    private const string EnvironmentFileScopePrefix = "actio-env-files-";
    private readonly string _environmentFileScopePath = Path.Combine(
        Path.GetTempPath(),
        $"{EnvironmentFileScopePrefix}{Guid.NewGuid():N}");
    private readonly object _environmentFileGate = new();

    internal string EnvironmentFileScopePath => _environmentFileScopePath;

    public string CreateRunId()
    {
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
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
        lock (_environmentFileGate)
        {
            EnsureEnvironmentFileScope();
            var directory = Path.Combine(
                _environmentFileScopePath,
                SanitizePathSegment(runId),
                SanitizePathSegment(jobName),
                $"{stepIndex + 1:D3}-{SanitizePathSegment(stepName)}");

            Directory.CreateDirectory(directory);
            return Task.FromResult(CreateEnvironmentFiles(directory));
        }
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

    public Task<ArtifactSaveResult> SaveArtifactAsync(
        string runId,
        string jobName,
        string projectRoot,
        string artifactName,
        IReadOnlyList<string> paths,
        int? retentionDays = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ArtifactSaveResult([], []));
    }

    public Task<ArtifactDownloadResult> RestoreArtifactsAsync(
        string projectRoot,
        IReadOnlyList<WorkflowRunArtifact> artifacts,
        string destinationPath,
        bool useArtifactNameSubdirectories,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ArtifactDownloadResult([], []));
    }

    internal void CleanupEnvironmentFiles(string runId)
    {
        lock (_environmentFileGate)
        {
            try
            {
                var tempRoot = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var scopePath = Path.GetFullPath(_environmentFileScopePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!scopePath.StartsWith(tempRoot + Path.DirectorySeparatorChar, comparison) ||
                    !Path.GetFileName(scopePath).StartsWith(EnvironmentFileScopePrefix, StringComparison.Ordinal))
                {
                    return;
                }

                var scope = new DirectoryInfo(scopePath);
                if (!scope.Exists)
                {
                    return;
                }

                if ((scope.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                var runPath = Path.GetFullPath(Path.Combine(scopePath, SanitizePathSegment(runId)))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!runPath.StartsWith(scopePath + Path.DirectorySeparatorChar, comparison))
                {
                    return;
                }

                var runDirectory = new DirectoryInfo(runPath);
                if (runDirectory.Exists)
                {
                    runDirectory.Delete(recursive: (runDirectory.Attributes & FileAttributes.ReparsePoint) == 0);
                }

                if (!scope.EnumerateFileSystemInfos().Any())
                {
                    scope.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public Task RequestRunCancellationAsync(string runId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> IsRunCancellationRequestedAsync(string runId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
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

    private void EnsureEnvironmentFileScope()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_environmentFileScopePath);
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        Directory.CreateDirectory(_environmentFileScopePath, ownerOnly);
        File.SetUnixFileMode(_environmentFileScopePath, ownerOnly);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidChars.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".."
            ? "unnamed"
            : sanitized;
    }
}
