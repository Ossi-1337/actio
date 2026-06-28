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
    private readonly FileSystemActionCache _actionCache;
    private readonly WorkflowParser _workflowParser;
    private readonly TimeProvider _timeProvider;

    public ActioWebDataService(ActioWebOptions options)
        : this(
            options,
            new FileSystemRunStore(options.ActioHome),
            new FileSystemActionCache(options.ActioHome),
            new WorkflowParser(),
            TimeProvider.System)
    {
    }

    public ActioWebDataService(
        ActioWebOptions options,
        FileSystemRunStore runStore,
        FileSystemActionCache actionCache,
        WorkflowParser workflowParser,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _runStore = runStore;
        _actionCache = actionCache;
        _workflowParser = workflowParser;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ProjectRoot => Path.GetFullPath(_options.ProjectRoot);

    public string ActioHome => Path.GetFullPath(_options.ActioHome);

    public string ServerUrl => _options.Url;

    public string CacheRoot => _actionCache.ActionCachePath;

    public async Task<IReadOnlyList<WorkflowSummary>> GetWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        var runs = await GetProjectRunsAsync(cancellationToken);
        var workflows = new List<WorkflowSummary>();

        foreach (var workflowPath in EnumerateWorkflowFiles())
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
                FormatRunTrigger(run.RunTrigger),
                run.Jobs.Count,
                run.Artifacts.Count))
            .ToArray();
    }

    public async Task<WorkflowRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        try
        {
            var run = await _runStore.ReadRunRecordAsync(runId, cancellationToken);
            return run is not null && IsProjectRun(run) ? RefreshRunningDuration(run) : null;
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
            .FirstOrDefault(job =>
                string.Equals(job.Id, jobName, StringComparison.Ordinal) ||
                string.Equals(job.Name, jobName, StringComparison.Ordinal))?
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

    public async Task<CacheResult> GetCacheAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _actionCache.ListAsync(cancellationToken);
        return new CacheResult(CacheRoot, entries);
    }

    public async Task<CacheCleanResult> CleanCacheAsync(CancellationToken cancellationToken = default)
    {
        var removed = await _actionCache.CleanAsync(cancellationToken);
        return new CacheCleanResult(removed);
    }

    public async Task<string?> GetWorkflowFileAsync(string runId, CancellationToken cancellationToken = default)
    {
        var workflowFile = await GetWorkflowFileResultAsync(runId, cancellationToken);
        return workflowFile?.Content;
    }

    public async Task<WorkflowFileResult?> GetWorkflowFileResultAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var workflowPath = await ResolveWorkflowPathAsync(runId, cancellationToken);
        if (workflowPath is null)
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            return new WorkflowFileResult(Path.GetFileName(workflowPath), workflowPath, content);
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return null;
        }
    }

    public async Task<WorkflowFileUpdateResult> UpdateWorkflowFileAsync(
        string runId,
        string? content,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            return WorkflowFileUpdateResult.Failed(["Workflow file content is required."]);
        }

        var workflowPath = await ResolveWorkflowPathAsync(runId, cancellationToken);
        if (workflowPath is null)
        {
            return WorkflowFileUpdateResult.Failed(["Workflow file could not be resolved inside the project's .workflows or .github/workflows directory."]);
        }

        var parseResult = _workflowParser.Parse(new StringReader(content));
        if (!parseResult.Success)
        {
            return WorkflowFileUpdateResult.Failed(parseResult.Errors);
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(workflowPath)!,
            $".{Path.GetFileName(workflowPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, workflowPath, overwrite: true);
            return WorkflowFileUpdateResult.Saved();
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return WorkflowFileUpdateResult.Failed([$"Workflow file could not be saved: {ex.Message}"]);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<IReadOnlyList<WorkflowRunRecord>> GetProjectRunsAsync(CancellationToken cancellationToken)
    {
        var runs = await _runStore.ListRunRecordsAsync(cancellationToken);
        return runs.Where(IsProjectRun).Select(RefreshRunningDuration).ToArray();
    }

    private bool IsProjectRun(WorkflowRunRecord run)
    {
        return IsSamePath(run.ProjectRoot, ProjectRoot);
    }

    private async Task<string?> ResolveWorkflowPathAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        if (run?.WorkflowPath is null ||
            !File.Exists(run.WorkflowPath) ||
            !IsWorkflowFile(run.WorkflowPath) ||
            !IsUnderWorkflowRoot(run.WorkflowPath))
        {
            return null;
        }

        return Path.GetFullPath(run.WorkflowPath);
    }

    private string ReadWorkflowDisplayName(string workflowPath)
    {
        var parseResult = _workflowParser.ParseFile(workflowPath);
        return parseResult.Success
            ? parseResult.Workflow!.Name
            : Path.GetFileNameWithoutExtension(workflowPath);
    }

    private IEnumerable<string> EnumerateWorkflowFiles()
    {
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workflowDirectory in WorkflowDirectories)
        {
            if (!Directory.Exists(workflowDirectory))
            {
                continue;
            }

            foreach (var workflowPath in Directory
                .EnumerateFiles(workflowDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsWorkflowFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (seenFileNames.Add(Path.GetFileName(workflowPath)))
                {
                    yield return workflowPath;
                }
            }
        }
    }

    private bool IsUnderWorkflowRoot(string path)
        => WorkflowDirectories.Any(directory => IsUnderRoot(path, directory));

    private string ActioWorkflowDirectory => Path.Combine(ProjectRoot, WorkflowFileResolver.ActioWorkflowDirectoryName);

    private string GitHubWorkflowDirectory => Path.Combine(ProjectRoot, WorkflowFileResolver.GitHubWorkflowDirectoryName);

    private IEnumerable<string> WorkflowDirectories
    {
        get
        {
            yield return ActioWorkflowDirectory;
            yield return GitHubWorkflowDirectory;
        }
    }

    private static bool IsWorkflowFile(string path)
    {
        return path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private WorkflowRunRecord RefreshRunningDuration(WorkflowRunRecord run)
    {
        if (!string.Equals(run.Status, "Running", StringComparison.Ordinal))
        {
            return run;
        }

        var now = _timeProvider.GetUtcNow();
        return run with
        {
            FinishedAt = now,
            DurationMilliseconds = Math.Max(0, (long)(now - run.StartedAt).TotalMilliseconds)
        };
    }

    private static string FormatRunTrigger(WorkflowRunTrigger trigger)
        => string.IsNullOrWhiteSpace(trigger.Source)
            ? trigger.EventName
            : $"{trigger.EventName} ({trigger.Source})";

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
