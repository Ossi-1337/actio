using Actio.Core.Actions;
using Actio.Core.Security;
using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Caching;
using Actio.Engine.Configuration;
using Actio.Engine.Execution;
using Actio.Engine.Runs;
using Actio.Runner.Docker;
using Actio.Storage;
using Actio.Web;

namespace Actio.Cli;

public sealed class CliApplication
{
    private readonly WorkflowFileResolver _resolver;
    private readonly WorkflowParser _parser;
    private readonly IWorkflowExecutor _executor;
    private readonly CliParser _cliParser;
    private readonly ILocalWebServerLauncher _webServerLauncher;
    private readonly IActionCache _actionCache;
    private readonly IDependencyCache _dependencyCache;
    private readonly CliOutputFormatter _outputFormatter;
    private readonly FileSystemLocalValueProvider _localValueProvider;
    private readonly FileSystemRunStore _runStore;
    private readonly Func<string> _createRunId;
    private readonly IActioConfigurationProvider _configurationProvider;

    public CliApplication()
        : this(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            CreateDefaultExecutor(),
            new CliParser(),
            new LocalWebServerLauncher(),
            new FileSystemActionCache(),
            new FileSystemDependencyCache(),
            new CliOutputFormatter(),
            new FileSystemLocalValueProvider(),
            configurationProvider: new FileSystemActioConfigurationProvider())
    {
    }

    public CliApplication(
        WorkflowFileResolver resolver,
        WorkflowParser parser,
        IWorkflowExecutor executor,
        CliParser? cliParser = null,
        ILocalWebServerLauncher? webServerLauncher = null,
        IActionCache? actionCache = null,
        IDependencyCache? dependencyCache = null,
        CliOutputFormatter? outputFormatter = null,
        FileSystemLocalValueProvider? localValueProvider = null,
        FileSystemRunStore? runStore = null,
        Func<string>? createRunId = null,
        IActioConfigurationProvider? configurationProvider = null)
    {
        _resolver = resolver;
        _parser = parser;
        _executor = executor;
        _cliParser = cliParser ?? new CliParser();
        _webServerLauncher = webServerLauncher ?? new LocalWebServerLauncher();
        _actionCache = actionCache ?? NullActionCache.Instance;
        _dependencyCache = dependencyCache ?? NullDependencyCache.Instance;
        _outputFormatter = outputFormatter ?? new CliOutputFormatter();
        _localValueProvider = localValueProvider ?? new FileSystemLocalValueProvider();
        _runStore = runStore ?? new FileSystemRunStore();
        _createRunId = createRunId ?? _runStore.CreateRunId;
        _configurationProvider = configurationProvider ?? new FileSystemActioConfigurationProvider();
    }

    public int Run(string[] args, string workingDirectory, TextWriter output, TextWriter error)
    {
        return RunAsync(args, workingDirectory, output, error).GetAwaiter().GetResult();
    }

    public async Task<int> RunAsync(
        string[] args,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var command = _cliParser.Parse(args);

        switch (command.Kind)
        {
            case CliCommandKind.ShowRootHelp:
                output.WriteLine(CliHelpText.Root);
                return ExitCodes.Success;
            case CliCommandKind.ShowRunHelp:
                output.WriteLine(CliHelpText.Run);
                return ExitCodes.Success;
            case CliCommandKind.ShowRerunHelp:
                output.WriteLine(CliHelpText.Rerun);
                return ExitCodes.Success;
            case CliCommandKind.ShowCancelHelp:
                output.WriteLine(CliHelpText.Cancel);
                return ExitCodes.Success;
            case CliCommandKind.ShowStatusHelp:
                output.WriteLine(CliHelpText.Status);
                return ExitCodes.Success;
            case CliCommandKind.ShowWebHelp:
                output.WriteLine(CliHelpText.Web);
                return ExitCodes.Success;
            case CliCommandKind.ShowCacheHelp:
                output.WriteLine(CliHelpText.Cache);
                return ExitCodes.Success;
            case CliCommandKind.ShowCompatibilityHelp:
                output.WriteLine(CliHelpText.Compatibility);
                return ExitCodes.Success;
            case CliCommandKind.ShowVersion:
                output.WriteLine($"actio {CliVersion.GetVersion()}");
                return ExitCodes.Success;
            case CliCommandKind.UsageError:
                WriteUsageError(error, command.ErrorMessage!);
                return ExitCodes.UsageError;
            case CliCommandKind.RunWorkflow:
                return await RunWorkflowAsync(command, workingDirectory, output, error, cancellationToken);
            case CliCommandKind.RerunWorkflow:
                return await RerunWorkflowAsync(command, output, error, cancellationToken);
            case CliCommandKind.CancelRun:
                return await CancelRunAsync(command, output, error, cancellationToken);
            case CliCommandKind.ShowRunStatus:
                return await ShowRunStatusAsync(command, output, error, cancellationToken);
            case CliCommandKind.RunWeb:
                return await RunWebAsync(command, workingDirectory, output, error, cancellationToken);
            case CliCommandKind.ListCache:
                return await ListCacheAsync(output, error, cancellationToken);
            case CliCommandKind.CleanCache:
                return await CleanCacheAsync(output, error, cancellationToken);
            case CliCommandKind.ShowCompatibility:
                output.WriteLine(ActionCompatibilityFormatter.Format(KnownActionCompatibilityCatalog.Entries));
                return ExitCodes.Success;
            default:
                throw new InvalidOperationException($"Unsupported CLI command kind '{command.Kind}'.");
        }
    }

    private static IWorkflowExecutor CreateDefaultExecutor()
    {
        var runStore = new FileSystemRunStore();
        return new WorkflowExecutor(new DockerRunnerProvider(), runStore, new FileSystemActionCache(), new FileSystemDependencyCache());
    }

    private async Task<int> RunWorkflowAsync(
        CliCommand command,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var resolution = _resolver.Resolve(command.WorkflowName!, workingDirectory);
        if (!resolution.Success)
        {
            WriteErrors(error, resolution.Errors);
            return ExitCodes.ValidationError;
        }

        var parseResult = _parser.ParseFile(resolution.WorkflowPath!);
        if (!parseResult.Success)
        {
            WriteErrors(error, parseResult.Errors);
            return ExitCodes.ValidationError;
        }

        WriteWarnings(error, parseResult.Warnings);

        var workflow = parseResult.Workflow!;
        return await ExecuteWorkflowAsync(
            workflow,
            resolution.ProjectRoot!,
            resolution.WorkflowPath,
            command.Inputs,
            "CLI",
            command.SecurityProfile,
            output,
            error,
            cancellationToken);
    }

    private async Task<int> RerunWorkflowAsync(
        CliCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var sourceRun = await ReadRunRecordAsync(command.RunId!, error, cancellationToken);
        if (sourceRun is null)
        {
            return ExitCodes.ValidationError;
        }

        if (string.Equals(sourceRun.Status, "Running", StringComparison.Ordinal))
        {
            error.WriteLine($"Run '{sourceRun.RunId}' is still running and cannot be rerun yet.");
            return ExitCodes.ValidationError;
        }

        if (sourceRun.WorkflowPath is null || !File.Exists(sourceRun.WorkflowPath))
        {
            error.WriteLine($"Run '{sourceRun.RunId}' cannot be rerun because its workflow file is missing.");
            return ExitCodes.ValidationError;
        }

        var parseResult = _parser.ParseFile(sourceRun.WorkflowPath);
        if (!parseResult.Success)
        {
            WriteErrors(error, parseResult.Errors);
            return ExitCodes.ValidationError;
        }

        WriteWarnings(error, parseResult.Warnings);
        return await ExecuteWorkflowAsync(
            parseResult.Workflow!,
            sourceRun.ProjectRoot,
            sourceRun.WorkflowPath,
            sourceRun.RunTrigger.Inputs,
            $"rerun:{sourceRun.RunId}",
            sourceRun.RunnerSecurity?.RequestedProfile ?? RunnerSecurityProfiles.SecureBaseline,
            output,
            error,
            cancellationToken);
    }

    private async Task<int> CancelRunAsync(
        CliCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var run = await ReadRunRecordAsync(command.RunId!, error, cancellationToken);
        if (run is null)
        {
            return ExitCodes.ValidationError;
        }

        if (!string.Equals(run.Status, "Running", StringComparison.Ordinal))
        {
            error.WriteLine($"Run '{run.RunId}' is not running; current status is {run.Status}.");
            return ExitCodes.ValidationError;
        }

        try
        {
            await _runStore.RequestRunCancellationAsync(run.RunId, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableRunStoreError(ex))
        {
            error.WriteLine($"Run '{run.RunId}' could not be cancelled: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        output.WriteLine($"Cancellation requested for run {run.RunId}.");
        return ExitCodes.Success;
    }

    private async Task<int> ShowRunStatusAsync(
        CliCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var run = await ReadRunRecordAsync(command.RunId!, error, cancellationToken);
        if (run is null)
        {
            return ExitCodes.ValidationError;
        }

        output.WriteLine($"Run: {run.RunId}");
        output.WriteLine($"Workflow: {run.WorkflowName}");
        output.WriteLine($"Status: {run.Status}");
        output.WriteLine($"Started: {run.StartedAt:O}");
        output.WriteLine($"Duration: {run.DurationMilliseconds} ms");
        output.WriteLine($"Jobs: {run.Jobs.Count}");
        output.WriteLine($"Artifacts: {run.Artifacts.Count}");
        output.WriteLine($"Workflow file: {run.WorkflowPath ?? "Unknown"}");
        return ExitCodes.Success;
    }

    private async Task<int> ExecuteWorkflowAsync(
        WorkflowDocument workflow,
        string projectRoot,
        string? workflowPath,
        IReadOnlyDictionary<string, string> inputs,
        string triggerSource,
        string securityProfile,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (workflow.IsReusableOnly)
        {
            error.WriteLine($"Workflow '{workflow.Name}' is reusable through workflow_call and cannot be run directly yet.");
            error.WriteLine("Reusable workflow caller jobs are planned for a later milestone.");
            return ExitCodes.ValidationError;
        }

        var inputResolution = WorkflowDispatchInputResolver.Resolve(workflow, inputs);
        if (!inputResolution.Success)
        {
            WriteErrors(error, inputResolution.Errors);
            return ExitCodes.ValidationError;
        }

        var localValues = _localValueProvider.Load(projectRoot);
        if (!localValues.Success)
        {
            WriteErrors(error, localValues.Errors);
            return ExitCodes.ValidationError;
        }

        var configuration = _configurationProvider.Load();
        if (!configuration.Success)
        {
            WriteErrors(error, configuration.Errors);
            return ExitCodes.ValidationError;
        }

        var runId = _createRunId();
        var wrotePipelineLink = await WriteViewPipelineLinkAsync(
            projectRoot,
            runId,
            output,
            error,
            addLeadingSeparator: false,
            cancellationToken);

        if (wrotePipelineLink)
        {
            output.WriteLine();
        }

        var executionResult = await _executor.ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(
                projectRoot,
                workflowPath,
                runId,
                new WorkflowRunTrigger("workflow_dispatch", triggerSource, inputResolution.Inputs),
                Secrets: localValues.Values.Secrets,
                Variables: localValues.Values.Variables,
                RunnerPolicy: new RunnerExecutionPolicy(
                    securityProfile,
                    configuration.Configuration,
                    configuration.InstanceIdentity)),
            output,
            error,
            cancellationToken);

        if (!executionResult.Success)
        {
            WriteExecutionErrors(
                error,
                executionResult.Errors,
                executionResult.Status == WorkflowExecutionStatus.Cancelled
                    ? "Workflow execution cancelled:"
                    : "Workflow execution failed:");
            output.WriteLine(FormatSummary(executionResult.Status.ToString(), executionResult, output));
            WriteOutputsAndArtifacts(output, executionResult, addLeadingSeparator: true);
            return ExitCodes.ValidationError;
        }

        output.WriteLine(FormatSummary(executionResult.Status.ToString(), executionResult, output));
        WriteOutputsAndArtifacts(output, executionResult, addLeadingSeparator: true);
        return ExitCodes.Success;
    }

    private async Task<int> RunWebAsync(
        CliCommand command,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var projectRoot = command.ProjectRoot ?? _resolver.FindProjectRoot(workingDirectory);
        var actioHome = command.ActioHome ?? ActioHome.Resolve();
        var url = command.Url ?? ActioWebDefaults.DefaultUrl;

        if (!command.Background)
        {
            output.WriteLine($"Actio web UI listening on {url}");
        }

        try
        {
            await new ActioWebServer().RunAsync(new ActioWebOptions(projectRoot, actioHome, url, command.Background), cancellationToken);
            return ExitCodes.Success;
        }
        catch (IOException ex)
        {
            error.WriteLine($"Actio web UI failed: {ex.Message}");
            return ExitCodes.ValidationError;
        }
    }

    private async Task<int> ListCacheAsync(
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ActionCacheEntry> actionEntries;
        IReadOnlyList<DependencyCacheEntry> dependencyEntries;
        try
        {
            actionEntries = await _actionCache.ListAsync(cancellationToken);
            dependencyEntries = await _dependencyCache.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableCacheError(ex))
        {
            error.WriteLine($"Cache could not be listed: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        if (actionEntries.Count == 0 && dependencyEntries.Count == 0)
        {
            output.WriteLine("No cache entries.");
            return ExitCodes.Success;
        }

        output.WriteLine("cache:");

        if (actionEntries.Count > 0)
        {
            output.WriteLine(" action:");

            foreach (var entry in actionEntries)
            {
                output.WriteLine($" - {entry.Kind}:{entry.Uses}");
                output.WriteLine($"   key: {entry.Key}");
                output.WriteLine($"   source: {entry.SourcePath}");
                if (entry.PinnedIdentity is not null)
                {
                    output.WriteLine($"   pinned: {entry.PinnedIdentity}");
                }

                if (entry.MutablePart is not null)
                {
                    output.WriteLine($"   mutable: {entry.MutablePart}");
                }

                output.WriteLine($"   path: {entry.CachePath}");
                output.WriteLine($"   last used: {entry.LastUsedAt:O}");
            }
        }

        if (dependencyEntries.Count > 0)
        {
            output.WriteLine(" dependency:");

            foreach (var entry in dependencyEntries)
            {
                output.WriteLine($" - {entry.Key}");
                output.WriteLine($"   version: {entry.Version}");
                output.WriteLine($"   paths: {string.Join(", ", entry.Paths)}");
                output.WriteLine($"   path: {entry.CachePath}");
                output.WriteLine($"   last used: {entry.LastUsedAt:O}");
            }
        }

        return ExitCodes.Success;
    }

    private async Task<int> CleanCacheAsync(
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        int removed;
        try
        {
            removed = await _actionCache.CleanAsync(cancellationToken);
            removed += await _dependencyCache.CleanAsync(cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableCacheError(ex))
        {
            error.WriteLine($"Cache could not be cleaned: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        output.WriteLine($"Removed {removed} cache entr{(removed == 1 ? "y" : "ies")}.");
        return ExitCodes.Success;
    }

    private static bool IsRecoverableCacheError(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException;
    }

    private static bool IsRecoverableRunStoreError(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or System.Text.Json.JsonException
            or NotSupportedException
            or ArgumentException;
    }

    private async Task<WorkflowRunRecord?> ReadRunRecordAsync(
        string runId,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await _runStore.ReadRunRecordAsync(runId, cancellationToken);
            if (run is not null)
            {
                return run;
            }

            error.WriteLine($"Run '{runId}' was not found.");
            return null;
        }
        catch (Exception ex) when (IsRecoverableRunStoreError(ex))
        {
            error.WriteLine($"Run '{runId}' could not be read: {ex.Message}");
            return null;
        }
    }

    private static void WriteUsageError(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("Run 'actio --help' for usage.");
    }

    private static void WriteErrors(TextWriter error, IReadOnlyList<string> errors)
    {
        error.WriteLine("Workflow validation failed:");

        foreach (var item in errors)
        {
            error.WriteLine($" - {item}");
        }
    }

    private static void WriteWarnings(TextWriter error, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        error.WriteLine("Workflow warnings:");

        foreach (var item in warnings)
        {
            error.WriteLine($" - {item}");
        }

        error.WriteLine();
    }

    private static void WriteExecutionErrors(
        TextWriter error,
        IReadOnlyList<string> errors,
        string heading)
    {
        if (errors.Count == 0)
        {
            return;
        }

        error.WriteLine(heading);

        foreach (var item in errors)
        {
            error.WriteLine($" - {item}");
        }

        error.WriteLine();
    }

    private void WriteOutputsAndArtifacts(
        TextWriter output,
        WorkflowExecutionResult result,
        bool addLeadingSeparator)
    {
        var wrotePreviousSection = addLeadingSeparator;

        if (result.Outputs.Count > 0)
        {
            WriteSectionBreakIfNeeded(output, wrotePreviousSection);
            output.WriteLine("output:");

            foreach (var item in result.Outputs)
            {
                output.WriteLine($" - {item.JobName}.{item.Name}={item.Value}");
            }

            wrotePreviousSection = true;
        }

        if (result.Artifacts.Count > 0)
        {
            WriteSectionBreakIfNeeded(output, wrotePreviousSection);
            output.WriteLine("artifacts:");

            foreach (var item in result.Artifacts)
            {
                output.WriteLine($" - {item.Name}: {_outputFormatter.FormatFilePath(item.StoredPath, output)}");
            }

            wrotePreviousSection = true;
        }

        if (result.SecurityFindings.Count > 0)
        {
            WriteSectionBreakIfNeeded(output, wrotePreviousSection);
            output.WriteLine("security:");

            foreach (var finding in result.SecurityFindings)
            {
                output.WriteLine($" - {finding.Severity}: {finding.Location}: {finding.Message}");
                output.WriteLine($"   recommendation: {finding.Recommendation}");
            }
        }
    }

    private async Task<bool> WriteViewPipelineLinkAsync(
        string projectRoot,
        string? runId,
        TextWriter output,
        TextWriter error,
        bool addLeadingSeparator,
        CancellationToken cancellationToken)
    {
        if (runId is null)
        {
            return false;
        }

        var url = await _webServerLauncher.EnsureStartedAsync(projectRoot, runId, error, cancellationToken);
        if (url is not null)
        {
            WriteSectionBreakIfNeeded(output, addLeadingSeparator);
            output.WriteLine($"View pipeline: {url}");
            return true;
        }

        return false;
    }

    private string FormatSummary(string status, WorkflowExecutionResult result, TextWriter output)
    {
        var details = new List<string>();

        if (result.FailedSteps > 0)
        {
            details.Add($"{result.FailedSteps} failed");
        }

        if (result.SkippedSteps > 0)
        {
            details.Add($"{result.SkippedSteps} skipped");
        }

        if (result.ContinuedSteps > 0)
        {
            details.Add($"{result.ContinuedSteps} continued");
        }

        if (!result.Success && result.FailedSteps == 0)
        {
            details.Add(result.Status == WorkflowExecutionStatus.Cancelled
                ? "workflow cancelled"
                : "workflow error");
        }

        var suffix = details.Count == 0 ? string.Empty : $", {string.Join(", ", details)}";
        return $"{_outputFormatter.FormatStatus(status, output)} ({result.SuccessfulSteps} / {result.TotalSteps}{suffix})";
    }

    private static void WriteSectionBreakIfNeeded(TextWriter output, bool wrotePreviousSection)
    {
        if (wrotePreviousSection)
        {
            output.WriteLine();
        }
    }

}
