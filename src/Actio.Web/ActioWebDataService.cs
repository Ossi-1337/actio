using Actio.Core.Workflows;
using Actio.Core.IO;
using Actio.Engine.Execution;
using Actio.Engine.Runs;
using Actio.Runner.Docker;
using Actio.Storage;
using Actio.Web.Models;
using System.Text.Json;

namespace Actio.Web;

public sealed class ActioWebDataService
{
    private readonly ActioWebOptions _options;
    private readonly FileSystemRunStore _runStore;
    private readonly FileSystemActionCache _actionCache;
    private readonly FileSystemDependencyCache _dependencyCache;
    private readonly WorkflowParser _workflowParser;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IWorkflowExecutor> _createExecutor;
    private readonly Func<Func<Task>, Task> _scheduleBackgroundWork;
    private readonly string _projectRoot;
    private readonly string _actioHome;

    public ActioWebDataService(ActioWebOptions options)
        : this(
            options,
            new FileSystemRunStore(options.ActioHome),
            new FileSystemActionCache(options.ActioHome),
            new FileSystemDependencyCache(options.ActioHome),
            new WorkflowParser(),
            TimeProvider.System)
    {
    }

    public ActioWebDataService(
        ActioWebOptions options,
        FileSystemRunStore runStore,
        FileSystemActionCache actionCache,
        FileSystemDependencyCache dependencyCache,
        WorkflowParser workflowParser,
        TimeProvider? timeProvider = null,
        Func<IWorkflowExecutor>? createExecutor = null,
        Func<Func<Task>, Task>? scheduleBackgroundWork = null)
    {
        _options = options;
        _projectRoot = CanonicalPath.ResolveExistingDirectory(options.ProjectRoot);
        _actioHome = Path.GetFullPath(options.ActioHome);
        _runStore = runStore;
        _actionCache = actionCache;
        _dependencyCache = dependencyCache;
        _workflowParser = workflowParser;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createExecutor = createExecutor ?? CreateDefaultExecutor;
        _scheduleBackgroundWork = scheduleBackgroundWork ?? ScheduleBackgroundWork;
    }

    public string ProjectRoot => _projectRoot;

    public string ActioHome => _actioHome;

    public string ServerUrl => _options.Url;

    public string CacheRoot => Path.Combine(ActioHome, "cache");

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
            .FirstOrDefault(item =>
                string.Equals(item.Id, stepName, StringComparison.Ordinal) ||
                string.Equals(item.Name, stepName, StringComparison.Ordinal));

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
        var actionEntries = await _actionCache.ListAsync(cancellationToken);
        var dependencyEntries = await _dependencyCache.ListAsync(cancellationToken);
        return new CacheResult(
            CacheRoot,
            actionEntries,
            _dependencyCache.DependencyCachePath,
            dependencyEntries);
    }

    public async Task<CacheCleanResult> CleanCacheAsync(CancellationToken cancellationToken = default)
    {
        var removed = await _actionCache.CleanAsync(cancellationToken);
        removed += await _dependencyCache.CleanAsync(cancellationToken);
        return new CacheCleanResult(removed);
    }

    public async Task<RunActionResult> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return RunActionResult.Failed([$"Run '{runId}' was not found."]);
        }

        if (!string.Equals(run.Status, "Running", StringComparison.Ordinal))
        {
            return RunActionResult.Failed([$"Run '{runId}' is not running; current status is {run.Status}."]);
        }

        try
        {
            await _runStore.RequestRunCancellationAsync(runId, cancellationToken);
            return RunActionResult.Completed();
        }
        catch (Exception ex) when (IsRecoverableFileReadError(ex))
        {
            return RunActionResult.Failed([$"Run '{runId}' could not be cancelled: {ex.Message}"]);
        }
    }

    public async Task<RunActionResult> RerunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var sourceRun = await GetRunAsync(runId, cancellationToken);
        if (sourceRun is null)
        {
            return RunActionResult.Failed([$"Run '{runId}' was not found."]);
        }

        if (string.Equals(sourceRun.Status, "Running", StringComparison.Ordinal))
        {
            return RunActionResult.Failed([$"Run '{runId}' is still running and cannot be rerun yet."]);
        }

        if (sourceRun.WorkflowPath is null || !File.Exists(sourceRun.WorkflowPath))
        {
            return RunActionResult.Failed([$"Run '{runId}' cannot be rerun because its workflow file is missing."]);
        }

        var parseResult = _workflowParser.ParseFile(sourceRun.WorkflowPath);
        if (!parseResult.Success)
        {
            return RunActionResult.Failed(parseResult.Errors);
        }

        var workflow = parseResult.Workflow!;
        if (workflow.IsReusableOnly)
        {
            return RunActionResult.Failed([$"Workflow '{workflow.Name}' is reusable through workflow_call and cannot be run directly."]);
        }

        var inputResolution = WorkflowDispatchInputResolver.Resolve(workflow, sourceRun.RunTrigger.Inputs);
        if (!inputResolution.Success)
        {
            return RunActionResult.Failed(inputResolution.Errors);
        }

        var localValues = new FileSystemLocalValueProvider().Load(sourceRun.ProjectRoot);
        if (!localValues.Success)
        {
            return RunActionResult.Failed(localValues.Errors);
        }

        var newRunId = _runStore.CreateRunId();
        var options = new WorkflowExecutionOptions(
            sourceRun.ProjectRoot,
            sourceRun.WorkflowPath,
            newRunId,
            new WorkflowRunTrigger("workflow_dispatch", $"rerun:{sourceRun.RunId}", inputResolution.Inputs),
            Secrets: localValues.Values.Secrets,
            Variables: localValues.Values.Variables);

        await _scheduleBackgroundWork(() => _createExecutor().ExecuteAsync(
            workflow,
            options,
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None));

        return RunActionResult.Accepted(newRunId);
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
        return CanonicalPath.AreEquivalent(run.ProjectRoot, ProjectRoot);
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

    private IWorkflowExecutor CreateDefaultExecutor()
    {
        return new WorkflowExecutor(
            new DockerRunnerProvider(),
            _runStore,
            _actionCache,
            _dependencyCache);
    }

    private static Task ScheduleBackgroundWork(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch
            {
            }
        });

        return Task.CompletedTask;
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
