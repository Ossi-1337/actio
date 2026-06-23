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
        ActioHomePath = Path.GetFullPath(actioHome);
    }

    public string ActioHomePath { get; }

    public string RunsPath => Path.Combine(ActioHomePath, "runs");

    public string LogsPath => Path.Combine(ActioHomePath, "logs");

    public string ArtifactsPath => Path.Combine(ActioHomePath, "artifacts");

    public string CachePath => Path.Combine(ActioHomePath, "cache");

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

    public async Task<string?> WriteStepLogAsync(
        string runId,
        string jobName,
        int stepIndex,
        string stepName,
        IReadOnlyList<string> outputLines,
        IReadOnlyList<string> errorLines,
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
        var lines = outputLines
            .Select(line => $"[stdout] {line}")
            .Concat(errorLines.Select(line => $"[stderr] {line}"))
            .ToArray();

        await File.WriteAllLinesAsync(logPath, lines, cancellationToken);
        return logPath;
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
        var fullProjectRoot = Path.GetFullPath(projectRoot);

        foreach (var artifact in artifacts)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(fullProjectRoot, artifact.Path));
            if (!IsUnderRoot(sourcePath, fullProjectRoot))
            {
                errors.Add($"workflow.jobs.{jobName}.artifacts.{artifact.Name} path must stay inside the project root.");
                continue;
            }

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                errors.Add($"workflow.jobs.{jobName}.artifacts.{artifact.Name} path '{artifact.Path}' does not exist.");
                continue;
            }

            var artifactDirectory = Path.Combine(
                ArtifactsPath,
                SanitizePathSegment(runId),
                SanitizePathSegment(jobName),
                SanitizePathSegment(artifact.Name));

            if (File.Exists(sourcePath))
            {
                Directory.CreateDirectory(artifactDirectory);
                var storedPath = Path.Combine(artifactDirectory, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, storedPath, overwrite: true);
                savedArtifacts.Add(new WorkflowRunArtifact(jobName, artifact.Name, sourcePath, storedPath));
                continue;
            }

            CopyDirectory(sourcePath, artifactDirectory);
            savedArtifacts.Add(new WorkflowRunArtifact(jobName, artifact.Name, sourcePath, artifactDirectory));
        }

        return Task.FromResult(new ArtifactSaveResult(savedArtifacts, errors));
    }

    public async Task SaveRunRecordAsync(
        WorkflowRunRecord runRecord,
        CancellationToken cancellationToken = default)
    {
        var runDirectory = Path.Combine(RunsPath, SanitizePathSegment(runRecord.RunId));
        Directory.CreateDirectory(runDirectory);

        var runPath = Path.Combine(runDirectory, "run.json");
        await using var stream = File.Create(runPath);
        await JsonSerializer.SerializeAsync(stream, runRecord, JsonOptions, cancellationToken);
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
