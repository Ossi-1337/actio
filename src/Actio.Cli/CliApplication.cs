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

    public CliApplication()
        : this(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            CreateDefaultExecutor(),
            new CliParser(),
            new LocalWebServerLauncher(),
            new FileSystemActionCache())
    {
    }

    public CliApplication(
        WorkflowFileResolver resolver,
        WorkflowParser parser,
        IWorkflowExecutor executor,
        CliParser? cliParser = null,
        ILocalWebServerLauncher? webServerLauncher = null,
        IActionCache? actionCache = null)
    {
        _resolver = resolver;
        _parser = parser;
        _executor = executor;
        _cliParser = cliParser ?? new CliParser();
        _webServerLauncher = webServerLauncher ?? new LocalWebServerLauncher();
        _actionCache = actionCache ?? NullActionCache.Instance;
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
                return await RunWorkflowAsync(command.WorkflowName!, workingDirectory, output, error, cancellationToken);
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
        string workflowName,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var resolution = _resolver.Resolve(workflowName, workingDirectory);
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

        var workflow = parseResult.Workflow!;
        var executionResult = await _executor.ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(resolution.ProjectRoot!, resolution.WorkflowPath),
            output,
            error,
            cancellationToken);

        if (!executionResult.Success)
        {
            WriteExecutionErrors(error, executionResult.Errors);
            output.WriteLine(FormatSummary("Failed", executionResult));
            WriteOutputsAndArtifacts(output, executionResult);
            await WriteViewPipelineLinkAsync(resolution.ProjectRoot!, executionResult.RunId, output, error, cancellationToken);
            return ExitCodes.ValidationError;
        }

        output.WriteLine(FormatSummary("Success", executionResult));
        WriteOutputsAndArtifacts(output, executionResult);
        await WriteViewPipelineLinkAsync(resolution.ProjectRoot!, executionResult.RunId, output, error, cancellationToken);
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
    }

    private static void WriteOutputsAndArtifacts(TextWriter output, WorkflowExecutionResult result)
    {
        if (result.Outputs.Count > 0)
        {
            output.WriteLine("output:");

            foreach (var item in result.Outputs)
            {
                output.WriteLine($" - {item.JobName}.{item.Name}={item.Value}");
            }
        }

        if (result.Artifacts.Count > 0)
        {
            output.WriteLine("artifacts:");

            foreach (var item in result.Artifacts)
            {
                output.WriteLine($" - {item.Name}: {item.StoredPath}");
            }
        }
    }

    private async Task WriteViewPipelineLinkAsync(
        string projectRoot,
        string? runId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (runId is null)
        {
            return;
        }

        var url = await _webServerLauncher.EnsureStartedAsync(projectRoot, runId, error, cancellationToken);
        if (url is not null)
        {
            output.WriteLine($"View pipeline: {url}");
        }
    }

    private static string FormatSummary(string status, WorkflowExecutionResult result)
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
        return $"{status} ({result.SuccessfulSteps} / {result.TotalSteps}{suffix})";
    }

}
