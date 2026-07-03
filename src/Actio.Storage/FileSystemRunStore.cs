using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Storage;

public sealed class FileSystemRunStore : IRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FileSystemRunStore()
        : this(ActioHome.Resolve())
    {
    }

    public FileSystemRunStore(string actioHome)
    {
        ActioHomePath = actioHome;
    }

    public string ActioHomePath { get; }

    public string RunsPath => Path.Combine(GetFullActioHomePath(), "runs");

    public string LogsPath => Path.Combine(GetFullActioHomePath(), "logs");

    public string ArtifactsPath => Path.Combine(GetFullActioHomePath(), "artifacts");

    public string CachePath => Path.Combine(GetFullActioHomePath(), "cache");

    public string CreateRunId()
    {
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
    }

    public Task<RunStoragePaths> InitializeRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(RunsPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(ArtifactsPath);

        var runDirectory = Path.Combine(RunsPath, SanitizePathSegment(runId));
        Directory.CreateDirectory(runDirectory);

        return Task.FromResult(new RunStoragePaths(
            runId,
            runDirectory,
            Path.Combine(runDirectory, "run.json")));
    }

    public async Task<IStepLog> OpenStepLogAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        CancellationToken cancellationToken = default)
    {
        var logDirectory = Path.Combine(
            LogsPath,
            SanitizePathSegment(runId),
            SanitizePathSegment(jobName));
        Directory.CreateDirectory(logDirectory);

        var logPath = Path.Combine(
            logDirectory,
            $"{stepIndex + 1:D3}-{SanitizePathSegment(stepName)}.log");
        var writer = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        await writer.FlushAsync(cancellationToken);
        return new FileSystemStepLog(logPath, writer);
    }

    public Task<StepEnvironmentFiles> CreateStepEnvironmentFilesAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            RunsPath,
            SanitizePathSegment(runId),
            "env-files",
            SanitizePathSegment(jobName),
            $"{stepIndex + 1:D3}-{SanitizePathSegment(stepName)}");
        Directory.CreateDirectory(directory);

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
        return Task.FromResult(files);
    }

    public Task<ArtifactSaveResult> SaveArtifactsAsync(
        string runId,
        string jobName,
        string projectRoot,
        IReadOnlyList<WorkflowArtifact> artifacts,
        CancellationToken cancellationToken = default)
    {
        var savedArtifacts = new List<WorkflowRunArtifact>();
        var errors = new List<string>();

        foreach (var artifact in artifacts)
        {
            var result = SaveArtifactCore(
                runId,
                jobName,
                projectRoot,
                artifact.Name,
                [artifact.Path],
                artifact.RetentionDays,
                $"workflow.jobs.{jobName}.artifacts.{artifact.Name}");
            savedArtifacts.AddRange(result.Artifacts);
            errors.AddRange(result.Errors);
        }

        return Task.FromResult(new ArtifactSaveResult(savedArtifacts, errors));
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
        return Task.FromResult(SaveArtifactCore(
            runId,
            jobName,
            projectRoot,
            artifactName,
            paths,
            retentionDays,
            $"actions/upload-artifact '{artifactName}'"));
    }

    public Task<ArtifactDownloadResult> RestoreArtifactsAsync(
        string projectRoot,
        IReadOnlyList<WorkflowRunArtifact> artifacts,
        string destinationPath,
        bool useArtifactNameSubdirectories,
        CancellationToken cancellationToken = default)
    {
        var restoredPaths = new List<string>();
        var errors = new List<string>();
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullDestinationPath = Path.GetFullPath(Path.Combine(fullProjectRoot, destinationPath));

        if (!IsUnderRoot(fullDestinationPath, fullProjectRoot))
        {
            return Task.FromResult(new ArtifactDownloadResult(
                [],
                [$"actions/download-artifact path '{destinationPath}' must stay inside the project root."]));
        }

        if (File.Exists(fullDestinationPath))
        {
            return Task.FromResult(new ArtifactDownloadResult(
                [],
                [$"actions/download-artifact path '{destinationPath}' must be a directory."]));
        }

        var fullArtifactsPath = Path.GetFullPath(ArtifactsPath);
        foreach (var artifact in artifacts)
        {
            var storedPath = Path.GetFullPath(artifact.StoredPath);
            if (!IsUnderRoot(storedPath, fullArtifactsPath))
            {
                errors.Add($"artifact '{artifact.Name}' stored path must stay inside Actio artifact storage.");
                continue;
            }

            if (!File.Exists(storedPath) && !Directory.Exists(storedPath))
            {
                errors.Add($"artifact '{artifact.Name}' stored path '{artifact.StoredPath}' does not exist.");
                continue;
            }

            var targetDirectory = useArtifactNameSubdirectories
                ? Path.Combine(fullDestinationPath, SanitizePathSegment(artifact.Name))
                : fullDestinationPath;

            if (File.Exists(storedPath))
            {
                Directory.CreateDirectory(targetDirectory);
                var targetPath = Path.Combine(targetDirectory, Path.GetFileName(storedPath));
                File.Copy(storedPath, targetPath, overwrite: true);
                restoredPaths.Add(targetPath);
                continue;
            }

            CopyDirectory(storedPath, targetDirectory);
            restoredPaths.Add(targetDirectory);
        }

        return Task.FromResult(new ArtifactDownloadResult(restoredPaths, errors));
    }

    public async Task RequestRunCancellationAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var runDirectory = Path.Combine(RunsPath, SanitizePathSegment(runId));
        Directory.CreateDirectory(runDirectory);
        var markerPath = GetCancellationMarkerPath(runId);
        await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    public Task<bool> IsRunCancellationRequestedAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(GetCancellationMarkerPath(runId)));
    }

    public async Task SaveRunRecordAsync(
        WorkflowRunRecord runRecord,
        CancellationToken cancellationToken = default)
    {
        var runDirectory = Path.Combine(RunsPath, SanitizePathSegment(runRecord.RunId));
        Directory.CreateDirectory(runDirectory);

        var runPath = Path.Combine(runDirectory, "run.json");
        var tempPath = Path.Combine(runDirectory, $"run.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, runRecord, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, runPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async Task<WorkflowRunRecord?> ReadRunRecordAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var runPath = Path.Combine(RunsPath, SanitizePathSegment(runId), "run.json");
        if (!File.Exists(runPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(runPath);
        return await JsonSerializer.DeserializeAsync<WorkflowRunRecord>(stream, JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowRunRecord>> ListRunRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RunsPath))
        {
            return [];
        }

        var records = new List<WorkflowRunRecord>();

        string[] runPaths;
        try
        {
            runPaths = Directory.EnumerateFiles(RunsPath, "run.json", SearchOption.AllDirectories).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var runPath in runPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = File.OpenRead(runPath);
                var record = await JsonSerializer.DeserializeAsync<WorkflowRunRecord>(stream, JsonOptions, cancellationToken);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return records
            .OrderByDescending(record => record.StartedAt)
            .ToArray();
    }

    private ArtifactSaveResult SaveArtifactCore(
        string runId,
        string jobName,
        string projectRoot,
        string artifactName,
        IReadOnlyList<string> paths,
        int? retentionDays,
        string errorPrefix)
    {
        var errors = new List<string>();
        var sourcePaths = new List<string>();
        var fullProjectRoot = Path.GetFullPath(projectRoot);

        if (paths.Count == 0)
        {
            return new ArtifactSaveResult([], [$"{errorPrefix} path is required."]);
        }

        foreach (var path in paths)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(fullProjectRoot, path));
            if (!IsUnderRoot(sourcePath, fullProjectRoot))
            {
                errors.Add($"{errorPrefix} path '{path}' must stay inside the project root.");
                continue;
            }

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                errors.Add($"{errorPrefix} path '{path}' does not exist.");
                continue;
            }

            sourcePaths.Add(sourcePath);
        }

        if (errors.Count > 0)
        {
            return new ArtifactSaveResult([], errors);
        }

        var artifactDirectory = Path.Combine(
            ArtifactsPath,
            SanitizePathSegment(runId),
            SanitizePathSegment(jobName),
            SanitizePathSegment(artifactName));
        string? storedPath = null;

        foreach (var sourcePath in sourcePaths)
        {
            if (File.Exists(sourcePath))
            {
                Directory.CreateDirectory(artifactDirectory);
                var targetPath = Path.Combine(artifactDirectory, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, targetPath, overwrite: true);
                storedPath ??= sourcePaths.Count == 1 ? targetPath : artifactDirectory;
                continue;
            }

            CopyDirectory(sourcePath, artifactDirectory);
            storedPath = artifactDirectory;
        }

        var sourcePathRecord = sourcePaths.Count == 1
            ? sourcePaths[0]
            : fullProjectRoot;
        var artifact = new WorkflowRunArtifact(
            jobName,
            artifactName,
            sourcePathRecord,
            storedPath ?? artifactDirectory,
            retentionDays,
            CreateArtifactAttestation(storedPath ?? artifactDirectory));

        return new ArtifactSaveResult([artifact], []);
    }

    private static WorkflowRunArtifactAttestation CreateArtifactAttestation(string storedPath)
    {
        var fullStoredPath = Path.GetFullPath(storedPath);
        var isSingleFile = File.Exists(fullStoredPath);
        string[] files = isSingleFile
            ? [fullStoredPath]
            : Directory.EnumerateFiles(fullStoredPath, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(fullStoredPath, path), StringComparer.Ordinal)
                .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;

        foreach (var file in files)
        {
            var relativePath = isSingleFile
                ? Path.GetFileName(file)
                : Path.GetRelativePath(fullStoredPath, file).Replace('\\', '/');
            var fileInfo = new FileInfo(file);
            totalBytes += fileInfo.Length;

            AppendHashString(hash, relativePath);
            AppendHashInt64(hash, fileInfo.Length);
            AppendHashFileContent(hash, file);
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new WorkflowRunArtifactAttestation(
            "actio.local-artifact-attestation.v1",
            "local-unsigned",
            "sha256",
            digest,
            totalBytes,
            files.Length,
            DateTimeOffset.UtcNow);
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendHashInt64(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendHashInt64(IncrementalHash hash, long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendHashFileContent(IncrementalHash hash, string file)
    {
        var buffer = new byte[81920];
        using var stream = File.OpenRead(file);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private string GetFullActioHomePath()
    {
        return Path.GetFullPath(ActioHomePath);
    }

    private string GetCancellationMarkerPath(string runId)
    {
        return Path.Combine(RunsPath, SanitizePathSegment(runId), "cancel.requested");
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);

        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
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
