using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Execution;
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
    private readonly CliOutputFormatter _outputFormatter;
    private readonly Func<string> _createRunId;

    public CliApplication()
        : this(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            CreateDefaultExecutor(),
            new CliParser(),
            new LocalWebServerLauncher(),
            new FileSystemActionCache(),
            new CliOutputFormatter(),
            new FileSystemRunStore().CreateRunId)
    {
    }

    public CliApplication(
        WorkflowFileResolver resolver,
        WorkflowParser parser,
        IWorkflowExecutor executor,
        CliParser? cliParser = null,
        ILocalWebServerLauncher? webServerLauncher = null,
        IActionCache? actionCache = null,
        CliOutputFormatter? outputFormatter = null,
        Func<string>? createRunId = null)
    {
        _resolver = resolver;
        _parser = parser;
        _executor = executor;
        _cliParser = cliParser ?? new CliParser();
        _webServerLauncher = webServerLauncher ?? new LocalWebServerLauncher();
        _actionCache = actionCache ?? NullActionCache.Instance;
        _outputFormatter = outputFormatter ?? new CliOutputFormatter();
        _createRunId = createRunId ?? new FileSystemRunStore().CreateRunId;
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
            case CliCommandKind.ShowWebHelp:
                output.WriteLine(CliHelpText.Web);
                return ExitCodes.Success;
            case CliCommandKind.ShowCacheHelp:
                output.WriteLine(CliHelpText.Cache);
                return ExitCodes.Success;
            case CliCommandKind.ShowVersion:
                output.WriteLine($"actio {CliVersion.GetVersion()}");
                return ExitCodes.Success;
            case CliCommandKind.UsageError:
                WriteUsageError(error, command.ErrorMessage!);
                return ExitCodes.UsageError;
            case CliCommandKind.RunWorkflow:
                return await RunWorkflowAsync(command, workingDirectory, output, error, cancellationToken);
            case CliCommandKind.RunWeb:
                return await RunWebAsync(command, workingDirectory, output, error, cancellationToken);
            case CliCommandKind.ListCache:
                return await ListCacheAsync(output, error, cancellationToken);
            case CliCommandKind.CleanCache:
                return await CleanCacheAsync(output, error, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported CLI command kind '{command.Kind}'.");
        }
    }

    private static IWorkflowExecutor CreateDefaultExecutor()
    {
        var runStore = new FileSystemRunStore();
        return new WorkflowExecutor(new DockerRunnerProvider(), runStore, new FileSystemActionCache());
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
        var runId = _createRunId();
        var wrotePipelineLink = await WriteViewPipelineLinkAsync(
            resolution.ProjectRoot!,
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
            new WorkflowExecutionOptions(resolution.ProjectRoot!, resolution.WorkflowPath, runId),
            output,
            error,
            cancellationToken);

        if (!executionResult.Success)
        {
            WriteExecutionErrors(error, executionResult.Errors);
            output.WriteLine(FormatSummary("Failed", executionResult, output));
            WriteOutputsAndArtifacts(output, executionResult, addLeadingSeparator: true);
            return ExitCodes.ValidationError;
        }

        output.WriteLine(FormatSummary("Success", executionResult, output));
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
        IReadOnlyList<ActionCacheEntry> entries;
        try
        {
            entries = await _actionCache.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableCacheError(ex))
        {
            error.WriteLine($"Cache could not be listed: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        if (entries.Count == 0)
        {
            output.WriteLine("No cache entries.");
            return ExitCodes.Success;
        }

        output.WriteLine("cache:");

        foreach (var entry in entries)
        {
            output.WriteLine($" - {entry.Kind}:{entry.Uses}");
            output.WriteLine($"   key: {entry.Key}");
            output.WriteLine($"   source: {entry.SourcePath}");
            output.WriteLine($"   path: {entry.CachePath}");
            output.WriteLine($"   last used: {entry.LastUsedAt:O}");
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

    private static void WriteExecutionErrors(TextWriter error, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        error.WriteLine("Workflow execution failed:");

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

        if (!result.Success && result.FailedSteps == 0)
        {
            details.Add("workflow error");
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
