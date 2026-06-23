using Actio.Core.Workflows;
using Actio.Engine.Runs;
using Actio.Storage;
using Actio.Web.Models;
using System.Text.Json;

namespace Actio.Web;

public sealed class ActioWebDataService
{
    private readonly ActioWebOptions _options;
    private readonly FileSystemRunStore _runStore;
    private readonly WorkflowParser _workflowParser;

    public ActioWebDataService(ActioWebOptions options)
        : this(options, new FileSystemRunStore(options.ActioHome), new WorkflowParser())
    {
    }

    public ActioWebDataService(
        ActioWebOptions options,
        FileSystemRunStore runStore,
        WorkflowParser workflowParser)
    {
        _options = options;
        _runStore = runStore;
        _workflowParser = workflowParser;
    }

    public string ProjectRoot => Path.GetFullPath(_options.ProjectRoot);

    public async Task<IReadOnlyList<WorkflowSummary>> GetWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        var workflowDirectory = Path.Combine(ProjectRoot, ".workflows");
        if (!Directory.Exists(workflowDirectory))
        {
            return [];
        }

        var runs = await GetProjectRunsAsync(cancellationToken);
        var workflows = new List<WorkflowSummary>();

        foreach (var workflowPath in Directory
            .EnumerateFiles(workflowDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsWorkflowFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workflowRuns = runs
                .Where(run => IsSamePath(run.WorkflowPath, workflowPath))
                .OrderByDescending(run => run.StartedAt)
                .ToArray();
            var latestRun = workflowRuns.FirstOrDefault();

            workflows.Add(new WorkflowSummary(
                ReadWorkflowDisplayName(workflowPath),
                Path.GetFileName(workflowPath),
                workflowPath,
                workflowRuns.Length,
                latestRun?.RunId,
                latestRun?.Status,
                latestRun?.StartedAt));
        }

        return workflows;
    }

    public async Task<IReadOnlyList<RunSummary>> GetRunsAsync(CancellationToken cancellationToken = default)
    {
        var runs = await GetProjectRunsAsync(cancellationToken);

        return runs.Select(run => new RunSummary(
                run.RunId,
                run.WorkflowName,
                run.WorkflowPath,
                run.Status,
                run.StartedAt,
                run.DurationMilliseconds,
                "CLI",
                run.Jobs.Count,
                run.Artifacts.Count))
            .ToArray();
    }

    public async Task<WorkflowRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        try
        {
            var run = await _runStore.ReadRunRecordAsync(runId, cancellationToken);
            return run is not null && IsProjectRun(run) ? run : null;
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return null;
        }
    }

    public async Task<LogResult?> GetStepLogAsync(
        string runId,
        string jobName,
        string stepName,
        CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        var step = run?.Jobs
            .FirstOrDefault(job => string.Equals(job.Name, jobName, StringComparison.Ordinal))?
            .Steps
            .FirstOrDefault(item => string.Equals(item.Name, stepName, StringComparison.Ordinal));

        if (step?.LogPath is null || !File.Exists(step.LogPath) || !IsUnderRoot(step.LogPath, _runStore.LogsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.Open(step.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return new LogResult(step.LogPath, await reader.ReadToEndAsync(cancellationToken));
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return null;
        }
    }

    public async Task<ArtifactResult?> GetArtifactAsync(
        string runId,
        string jobName,
        string artifactName,
        CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        var artifact = run?.Artifacts.FirstOrDefault(item =>
            string.Equals(item.JobName, jobName, StringComparison.Ordinal) &&
            string.Equals(item.Name, artifactName, StringComparison.Ordinal));

        if (artifact is null || !IsUnderRoot(artifact.StoredPath, _runStore.ArtifactsPath))
        {
            return null;
        }

        if (File.Exists(artifact.StoredPath))
        {
            return new ArtifactResult(
                artifact.StoredPath,
                true,
                GetContentType(artifact.StoredPath),
                []);
        }

        if (!Directory.Exists(artifact.StoredPath))
        {
            return null;
        }

        string[] entries;
        try
        {
            entries = Directory
                .EnumerateFileSystemEntries(artifact.StoredPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(artifact.StoredPath, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return null;
        }

        return new ArtifactResult(artifact.StoredPath, false, null, entries);
    }

    public async Task<string?> GetWorkflowFileAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        if (run?.WorkflowPath is null ||
            !File.Exists(run.WorkflowPath) ||
            !IsUnderRoot(run.WorkflowPath, ProjectRoot))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(run.WorkflowPath, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<WorkflowRunRecord>> GetProjectRunsAsync(CancellationToken cancellationToken)
    {
        var runs = await _runStore.ListRunRecordsAsync(cancellationToken);
        return runs.Where(IsProjectRun).ToArray();
    }

    private bool IsProjectRun(WorkflowRunRecord run)
    {
        return IsSamePath(run.ProjectRoot, ProjectRoot);
    }

    private string ReadWorkflowDisplayName(string workflowPath)
    {
        var parseResult = _workflowParser.ParseFile(workflowPath);
        return parseResult.Success
            ? parseResult.Workflow!.Name
            : Path.GetFileNameWithoutExtension(workflowPath);
    }

    private static bool IsWorkflowFile(string path)
    {
        return path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);

        return string.Equals(fullPath, fullRoot, comparison) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) ||
            fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".json" => "application/json",
            ".log" => "text/plain",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
    }

    private static bool IsRecoverableFileReadError(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException;
    }
}
