using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Actio.Core.IO;
using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Storage;

public sealed class FileSystemRunStore : IRunStore
{
    private const int MaximumArtifactStorageSegmentLength = 80;
    private const int ArtifactStorageHashLength = 24;
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

        var isolationDirectory = Path.Combine(runDirectory, "isolation");
        Directory.CreateDirectory(isolationDirectory);
        var workspaceMaskFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".actio/secrets.env"] = CreateEmptyMaskFile(isolationDirectory, "secrets.env.mask"),
            [".actio/vars.env"] = CreateEmptyMaskFile(isolationDirectory, "vars.env.mask")
        };

        return Task.FromResult(new RunStoragePaths(
            runId,
            runDirectory,
            Path.Combine(runDirectory, "run.json"),
            GetFullActioHomePath(),
            workspaceMaskFiles));
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
            if (!TryResolveArtifactDirectory(runId, jobName, artifact.Name, out _))
            {
                errors.Add($"workflow.jobs.{jobName}.artifacts.{artifact.Name} storage path must stay inside the run artifact directory.");
            }

            errors.AddRange(ValidateArtifactSourcePaths(
                projectRoot,
                [artifact.Path],
                $"workflow.jobs.{jobName}.artifacts.{artifact.Name}"));
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(new ArtifactSaveResult([], errors));
        }

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

            try
            {
                SafeFileTree.ValidateExistingPath(fullArtifactsPath, storedPath, "artifact restore");
                if (Directory.Exists(storedPath))
                {
                    SafeFileTree.Enumerate(storedPath, "artifact restore");
                }
            }
            catch (SafeFileTreeException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(new ArtifactDownloadResult([], errors));
        }

        foreach (var artifact in artifacts)
        {
            var storedPath = Path.GetFullPath(artifact.StoredPath);
            var targetDirectory = useArtifactNameSubdirectories
                ? Path.Combine(fullDestinationPath, CreateArtifactStorageSegment(artifact.Name))
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

            await ReplaceRunRecordAsync(tempPath, runPath, cancellationToken);
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

        return await ReadRunRecordFileAsync(runPath, cancellationToken);
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
                await using var stream = OpenRunRecordForRead(runPath);
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

        if (!TryResolveArtifactDirectory(runId, jobName, artifactName, out var artifactDirectory))
        {
            return new ArtifactSaveResult(
                [],
                [$"{errorPrefix} storage path must stay inside the run artifact directory."]);
        }

        errors.AddRange(ValidateArtifactSourcePaths(projectRoot, paths, errorPrefix));
        if (errors.Count > 0)
        {
            return new ArtifactSaveResult([], errors);
        }

        foreach (var path in paths)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(fullProjectRoot, path));
            sourcePaths.Add(sourcePath);
        }

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

    private static IReadOnlyList<string> ValidateArtifactSourcePaths(
        string projectRoot,
        IReadOnlyList<string> paths,
        string errorPrefix)
    {
        var errors = new List<string>();
        var fullProjectRoot = Path.GetFullPath(projectRoot);
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

            try
            {
                SafeFileTree.ValidateExistingPath(fullProjectRoot, sourcePath, "artifact save");
                if (Directory.Exists(sourcePath))
                {
                    SafeFileTree.Enumerate(sourcePath, "artifact save");
                }
            }
            catch (SafeFileTreeException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors;
    }

    private static WorkflowRunArtifactAttestation CreateArtifactAttestation(string storedPath)
    {
        var fullStoredPath = Path.GetFullPath(storedPath);
        var isSingleFile = File.Exists(fullStoredPath);
        string[] files = isSingleFile
            ? [fullStoredPath]
            : SafeFileTree.Enumerate(fullStoredPath, "artifact attestation")
                .Where(entry => !entry.IsDirectory)
                .Select(entry => entry.FullPath)
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
        var entries = SafeFileTree.Enumerate(sourceDirectory, "artifact copy");
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in entries.Where(entry => entry.IsDirectory))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, directory.RelativePath));
        }

        foreach (var sourceFile in entries.Where(entry => !entry.IsDirectory))
        {
            var targetFile = Path.Combine(targetDirectory, sourceFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile.FullPath, targetFile, overwrite: true);
        }
    }

    private string GetFullActioHomePath()
    {
        return Path.GetFullPath(ActioHomePath);
    }

    private bool TryResolveArtifactDirectory(
        string runId,
        string jobName,
        string artifactName,
        out string artifactDirectory)
    {
        artifactDirectory = string.Empty;
        if (IsNavigationSegment(runId) ||
            IsNavigationSegment(jobName) ||
            IsNavigationSegment(artifactName))
        {
            return false;
        }

        var artifactsRoot = Path.GetFullPath(ArtifactsPath);
        var runDirectory = Path.GetFullPath(Path.Combine(artifactsRoot, CreateArtifactStorageSegment(runId)));
        var jobDirectory = Path.GetFullPath(Path.Combine(runDirectory, CreateArtifactStorageSegment(jobName)));
        var candidate = Path.GetFullPath(Path.Combine(jobDirectory, CreateArtifactStorageSegment(artifactName)));
        if (!IsStrictlyUnderRoot(runDirectory, artifactsRoot) ||
            !IsStrictlyUnderRoot(jobDirectory, runDirectory) ||
            !IsStrictlyUnderRoot(candidate, jobDirectory))
        {
            return false;
        }

        artifactDirectory = candidate;
        return true;
    }

    private static FileStream OpenRunRecordForRead(string path)
        => File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private static async Task<WorkflowRunRecord?> ReadRunRecordFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = OpenRunRecordForRead(path);
                return await JsonSerializer.DeserializeAsync<WorkflowRunRecord>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (Exception ex) when (
                attempt < attempts &&
                ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }
    }

    private static async Task ReplaceRunRecordAsync(
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, null);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }

                return;
            }
            catch (Exception ex) when (
                attempt < attempts &&
                ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }
    }

    private static string CreateEmptyMaskFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
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

    private static bool IsStrictlyUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(normalizedPath, normalizedRoot, comparison) &&
            IsUnderRoot(normalizedPath, normalizedRoot);
    }

    private static bool IsNavigationSegment(string value)
        => value is "." or "..";

    private static string CreateArtifactStorageSegment(string value)
    {
        if (IsPortableArtifactStorageSegment(value))
        {
            return value;
        }

        var prefixLength = MaximumArtifactStorageSegmentLength - ArtifactStorageHashLength - 2;
        var prefix = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-')
            .ToArray());
        prefix = string.IsNullOrWhiteSpace(prefix) || IsNavigationSegment(prefix)
            ? "unnamed"
            : prefix[..Math.Min(prefix.Length, prefixLength)];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..ArtifactStorageHashLength];
        return $"~{prefix}-{hash}";
    }

    private static bool IsPortableArtifactStorageSegment(string value)
    {
        if (value.Length == 0 ||
            value.Length > MaximumArtifactStorageSegmentLength ||
            IsNavigationSegment(value) ||
            value.EndsWith('.') ||
            value.Any(character =>
                !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-' or '_')))
        {
            return false;
        }

        var stem = value.Split('.', 2)[0];
        return stem is not "con" and not "prn" and not "aux" and not "nul" &&
            !(stem.Length == 4 &&
                (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidChars.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) || IsNavigationSegment(sanitized)
            ? "unnamed"
            : sanitized;
    }
}
