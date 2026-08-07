using Actio.Core.Actions;
using Actio.Core.Security;
using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Caching;
using Actio.Engine.Configuration;
using Actio.Engine.Execution;
using Actio.Engine.Runs;
using Actio.Engine.Triggers;
using Actio.Engine.Validation;
using Actio.Git;
using Actio.Runner.Docker;
using Actio.Storage;
using Actio.Web;
using System.Diagnostics;

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
    private readonly WorkflowStaticValidator _staticValidator;
    private readonly IGitHookManager _gitHookManager;
    private readonly IGitRepositoryClient _gitRepository;

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
            configurationProvider: new FileSystemActioConfigurationProvider(),
            staticValidator: new WorkflowStaticValidator(),
            gitHookManager: new GitHookManager(),
            gitRepository: new GitRepositoryClient())
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
        IActioConfigurationProvider? configurationProvider = null,
        WorkflowStaticValidator? staticValidator = null,
        IGitHookManager? gitHookManager = null,
        IGitRepositoryClient? gitRepository = null)
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
        _staticValidator = staticValidator ?? new WorkflowStaticValidator();
        _gitRepository = gitRepository ?? new GitRepositoryClient();
        _gitHookManager = gitHookManager ?? new GitHookManager(_gitRepository);
    }

    public int Run(string[] args, string workingDirectory, TextWriter output, TextWriter error)
    {
        return RunAsync(args, workingDirectory, output, error).GetAwaiter().GetResult();
    }

    public int Run(
        string[] args,
        string workingDirectory,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        return RunAsync(args, workingDirectory, input, output, error).GetAwaiter().GetResult();
    }

    public async Task<int> RunAsync(
        string[] args,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(
            args,
            workingDirectory,
            TextReader.Null,
            output,
            error,
            cancellationToken);
    }

    public async Task<int> RunAsync(
        string[] args,
        string workingDirectory,
        TextReader input,
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
            case CliCommandKind.ShowValidateHelp:
                output.WriteLine(CliHelpText.Validate);
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
            case CliCommandKind.ShowHooksHelp:
                output.WriteLine(CliHelpText.Hooks);
                return ExitCodes.Success;
            case CliCommandKind.ShowVersion:
                output.WriteLine($"actio {CliVersion.GetVersion()}");
                return ExitCodes.Success;
            case CliCommandKind.UsageError:
                WriteUsageError(error, command.ErrorMessage!);
                return ExitCodes.UsageError;
            case CliCommandKind.RunWorkflow:
                return await RunWorkflowAsync(command, workingDirectory, output, error, cancellationToken);
            case CliCommandKind.ValidateWorkflow:
                return ValidateWorkflow(command, workingDirectory, output, error);
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
            case CliCommandKind.InstallHooks:
                return await RunHookLifecycleAsync(
                    () => _gitHookManager.InstallAsync(workingDirectory, cancellationToken),
                    output,
                    error);
            case CliCommandKind.ShowHooksStatus:
                return await RunHookLifecycleAsync(
                    () => _gitHookManager.GetStatusAsync(workingDirectory, cancellationToken),
                    output,
                    error);
            case CliCommandKind.UninstallHooks:
                return await RunHookLifecycleAsync(
                    () => _gitHookManager.UninstallAsync(workingDirectory, cancellationToken),
                    output,
                    error);
            case CliCommandKind.RunPrePushHook:
                return await RunPrePushHookAsync(
                    command,
                    workingDirectory,
                    input,
                    output,
                    error,
                    cancellationToken);
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
            null,
            output,
            error,
            cancellationToken,
            runTrigger: null,
            startWebServer: true);
    }

    private int ValidateWorkflow(
        CliCommand command,
        string workingDirectory,
        TextWriter output,
        TextWriter error)
    {
        var resolution = _resolver.Resolve(command.WorkflowName!, workingDirectory);
        if (!resolution.Success)
        {
            WriteErrors(error, resolution.Errors);
            return ExitCodes.ValidationError;
        }

        var localValues = _localValueProvider.Load(resolution.ProjectRoot!);
        if (!localValues.Success)
        {
            WriteErrors(error, localValues.Errors);
            return ExitCodes.ValidationError;
        }

        var configuration = _configurationProvider.Validate();
        if (!configuration.Success)
        {
            WriteErrors(error, configuration.Errors);
            return ExitCodes.ValidationError;
        }

        var validation = _staticValidator.Validate(
            resolution.WorkflowPath!,
            resolution.ProjectRoot!,
            command.Inputs,
            localValues.Values.Secrets);

        if (validation.Warnings.Count > 0)
        {
            error.WriteLine("Workflow warnings:");
            foreach (var warning in validation.Warnings)
            {
                error.WriteLine($" - {warning.SourcePath}: {warning.Message}");
            }
        }

        if (!validation.Success)
        {
            error.WriteLine("Workflow validation failed:");
            foreach (var validationError in validation.Errors)
            {
                error.WriteLine($" - {validationError.SourcePath}: {validationError.Message}");
            }

            return ExitCodes.ValidationError;
        }

        var workflow = validation.Workflow!;
        output.WriteLine($"Workflow '{workflow.Name}' is valid.");
        output.WriteLine($"Jobs: {workflow.Jobs.Count}");
        output.WriteLine($"Steps: {workflow.StepCount}");
        return ExitCodes.Success;
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
            sourceRun,
            output,
            error,
            cancellationToken,
            runTrigger: null,
            startWebServer: true);
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

    private static async Task<int> RunHookLifecycleAsync(
        Func<Task<GitHookResult>> operation,
        TextWriter output,
        TextWriter error)
    {
        GitHookResult result;
        try
        {
            result = await operation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error.WriteLine($"Git hook operation failed: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        (result.Success ? output : error).WriteLine(result.Message);
        if (result.HookPath is not null)
        {
            (result.Success ? output : error).WriteLine($"Hook: {result.HookPath}");
        }

        return result.Success ? ExitCodes.Success : ExitCodes.ValidationError;
    }

    private async Task<int> RunPrePushHookAsync(
        CliCommand command,
        string workingDirectory,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RemoteName))
        {
            error.WriteLine("Git pre-push remote name is invalid.");
            return ExitCodes.ValidationError;
        }

        var repositoryResult = await _gitRepository.InspectAsync(workingDirectory, cancellationToken);
        if (!repositoryResult.Success)
        {
            WriteErrors(error, repositoryResult.Errors);
            return ExitCodes.ValidationError;
        }

        var parsedInput = GitPrePushInputParser.Parse(await input.ReadToEndAsync(cancellationToken));
        if (!parsedInput.Success)
        {
            WriteErrors(error, parsedInput.Errors);
            return ExitCodes.ValidationError;
        }

        var projectRoot = repositoryResult.Value!.ProjectRoot;
        IReadOnlyList<PushWorkflowSource>? workflowSources;
        try
        {
            workflowSources = ParsePushWorkflowSources(projectRoot, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error.WriteLine($"Git pre-push workflows could not be read: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        if (workflowSources is null)
        {
            return ExitCodes.ValidationError;
        }

        var updates = parsedInput.Updates
            .Where(update => !update.IsDeletion && update.ReferenceKind != GitReferenceKind.Unsupported)
            .ToArray();
        var candidateUpdates = updates
            .Where(update => HasReferenceMatch(workflowSources, update))
            .ToArray();
        if (candidateUpdates.Length == 0)
        {
            output.WriteLine("No push-triggered workflows matched.");
            return ExitCodes.Success;
        }

        var cleanResult = await _gitRepository.IsCleanAsync(projectRoot, cancellationToken);
        if (!cleanResult.Success)
        {
            WriteErrors(error, cleanResult.Errors);
            return ExitCodes.ValidationError;
        }

        if (!cleanResult.Value)
        {
            error.WriteLine("Git pre-push validation requires a clean worktree, including no untracked files.");
            error.WriteLine("Commit, remove, or ignore local changes before pushing.");
            return ExitCodes.ValidationError;
        }

        var headResult = await _gitRepository.GetHeadAsync(projectRoot, cancellationToken);
        if (!headResult.Success)
        {
            WriteErrors(error, headResult.Errors);
            return ExitCodes.ValidationError;
        }

        var nonHeadUpdate = candidateUpdates.FirstOrDefault(update =>
            !string.Equals(update.LocalObjectId, headResult.Value, StringComparison.OrdinalIgnoreCase));
        if (nonHeadUpdate is not null)
        {
            error.WriteLine(
                $"Git pre-push validation supports only current HEAD. '{nonHeadUpdate.RemoteRef}' points to a different local object.");
            error.WriteLine("Check out the commit you intend to push, then retry.");
            return ExitCodes.ValidationError;
        }

        var references = new List<PushReferenceEvent>();
        foreach (var update in candidateUpdates)
        {
            var pathsResult = await _gitRepository.GetChangedPathsAsync(projectRoot, update, cancellationToken);
            if (!pathsResult.Success)
            {
                WriteErrors(error, pathsResult.Errors);
                return ExitCodes.ValidationError;
            }

            references.Add(new PushReferenceEvent(
                update.RemoteRef,
                update.ReferenceName,
                update.ReferenceKind == GitReferenceKind.Branch ? "branch" : "tag",
                update.RemoteObjectId,
                update.LocalObjectId,
                pathsResult.Value!));
        }

        var plan = PushWorkflowPlanner.Create(workflowSources, references);
        if (plan.Count == 0)
        {
            output.WriteLine("No push-triggered workflows matched.");
            return ExitCodes.Success;
        }

        var exitCode = ExitCodes.Success;
        var remoteName = IsSafeRemoteName(command.RemoteName, command.RemoteUrl)
            ? command.RemoteName
            : "direct";
        foreach (var entry in plan)
        {
            output.WriteLine(
                $"[pre-push] {entry.Source.Workflow.Name} ({entry.Reference.ReferenceType} {entry.Reference.ReferenceName})");

            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["remote"] = remoteName,
                ["ref"] = entry.Reference.FullReference,
                ["ref_name"] = entry.Reference.ReferenceName,
                ["ref_type"] = entry.Reference.ReferenceType,
                ["before"] = entry.Reference.BeforeSha,
                ["after"] = entry.Reference.AfterSha,
                ["new_ref"] = IsZeroObjectId(entry.Reference.BeforeSha) ? "true" : "false",
                ["diff_base"] = IsZeroObjectId(entry.Reference.BeforeSha) ? "HEAD" : entry.Reference.BeforeSha
            };
            var payload = WorkflowEventPayload.Create(
                "push",
                "Git pre-push",
                properties: properties);
            var trigger = new WorkflowRunTrigger(
                "push",
                "Git pre-push",
                EventPayload: payload);

            var workflowExitCode = await ExecuteWorkflowAsync(
                entry.Source.Workflow,
                projectRoot,
                entry.Source.WorkflowPath,
                new Dictionary<string, string>(),
                "Git pre-push",
                RunnerSecurityProfiles.SecureBaseline,
                null,
                output,
                error,
                cancellationToken,
                trigger,
                startWebServer: false);
            if (workflowExitCode != ExitCodes.Success)
            {
                exitCode = ExitCodes.ValidationError;
            }
        }

        return exitCode;
    }

    private IReadOnlyList<PushWorkflowSource>? ParsePushWorkflowSources(
        string projectRoot,
        TextWriter error)
    {
        var sources = new List<PushWorkflowSource>();
        var errors = new List<string>();

        foreach (var path in WorkflowFileCatalog.Discover(projectRoot))
        {
            var parseResult = _parser.ParseFile(path);
            var relativePath = Path.GetRelativePath(projectRoot, path);
            foreach (var warning in parseResult.Warnings)
            {
                error.WriteLine($"Workflow warning: {relativePath}: {warning}");
            }

            if (!parseResult.Success)
            {
                errors.AddRange(parseResult.Errors.Select(parseError => $"{relativePath}: {parseError}"));
                continue;
            }

            sources.Add(new PushWorkflowSource(path, parseResult.Workflow!));
        }

        if (errors.Count == 0)
        {
            return sources;
        }

        error.WriteLine("Git pre-push workflow validation failed:");
        foreach (var parseError in errors)
        {
            error.WriteLine($" - {parseError}");
        }

        return null;
    }

    private static bool HasReferenceMatch(
        IReadOnlyList<PushWorkflowSource> workflows,
        GitPushRefUpdate update)
    {
        var context = update.ReferenceKind == GitReferenceKind.Branch
            ? new WorkflowTriggerFilterContext("push", Branch: update.ReferenceName)
            : new WorkflowTriggerFilterContext("push", Tag: update.ReferenceName);

        return workflows
            .SelectMany(source => source.Workflow.Triggers)
            .Where(trigger => string.Equals(trigger.EventName, "push", StringComparison.Ordinal))
            .Any(trigger => WorkflowTriggerFilterEvaluator.EvaluateReference(trigger, context).Matches);
    }

    private static bool IsSafeRemoteName(string remoteName, string? remoteUrl)
    {
        if (string.Equals(remoteName, remoteUrl, StringComparison.Ordinal))
        {
            return false;
        }

        return remoteName.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');
    }

    private static bool IsZeroObjectId(string value)
        => value.Length > 0 && value.All(character => character == '0');

    private async Task<int> ExecuteWorkflowAsync(
        WorkflowDocument workflow,
        string projectRoot,
        string? workflowPath,
        IReadOnlyDictionary<string, string> inputs,
        string triggerSource,
        string securityProfile,
        WorkflowRunRecord? rerunSource,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        WorkflowRunTrigger? runTrigger,
        bool startWebServer)
    {
        if (workflow.IsReusableOnly)
        {
            error.WriteLine($"Workflow '{workflow.Name}' is reusable through workflow_call and cannot be run directly yet.");
            error.WriteLine("Reusable workflow caller jobs are planned for a later milestone.");
            return ExitCodes.ValidationError;
        }

        var inputResolution = runTrigger is null
            ? WorkflowDispatchInputResolver.Resolve(workflow, inputs)
            : WorkflowDispatchInputResolutionResult.Resolved(runTrigger.Inputs);
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
        var wrotePipelineLink = startWebServer && await WriteViewPipelineLinkAsync(
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
        else if (!startWebServer)
        {
            output.WriteLine($"Run: {runId}");
            output.WriteLine();
        }

        var executionOptions = rerunSource is null
            ? new WorkflowExecutionOptions(
                projectRoot,
                workflowPath,
                runId,
                runTrigger ?? new WorkflowRunTrigger("workflow_dispatch", triggerSource, inputResolution.Inputs),
                Secrets: localValues.Values.Secrets,
                Variables: localValues.Values.Variables,
                RunnerPolicy: new RunnerExecutionPolicy(
                    securityProfile,
                    configuration.Configuration,
                    configuration.InstanceIdentity))
            : WorkflowRerunOptionsFactory.Create(
                rerunSource,
                runId,
                inputResolution.Inputs,
                localValues.Values.Secrets,
                localValues.Values.Variables,
                configuration.Configuration,
                configuration.InstanceIdentity);
        var executionResult = await _executor.ExecuteAsync(
            workflow,
            executionOptions,
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

        WebRuntimeDescription runtime;
        try
        {
            runtime = WebRuntimeSnapshotManager.CreateCurrent().DescribeCurrent(cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableWebError(ex))
        {
            error.WriteLine($"Actio web UI runtime identity failed: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        if (!command.Background)
        {
            try
            {
                await new ActioWebServer().RunAsync(
                    new ActioWebOptions(
                        projectRoot,
                        actioHome,
                        url,
                        RuntimeIdentity: runtime.Identity),
                    cancellationToken);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (IsRecoverableWebError(ex))
            {
                error.WriteLine($"Actio web UI failed: {ex.Message}");
                return ExitCodes.ValidationError;
            }
        }

        var worker = ReadWebWorkerContext(
            projectRoot,
            actioHome,
            runtime.Identity,
            error);
        if (worker is null)
        {
            return ExitCodes.ValidationError;
        }

        var processStore = worker.SessionId is null
            ? new WebProcessMetadataStore(actioHome, url)
            : WebProcessMetadataStore.ForProject(actioHome, worker.SessionId);
        FileStream runtimeLock;
        try
        {
            runtimeLock = WebProcessMetadataStore.OpenRuntimeUsageLock(
                actioHome,
                runtime.Identity);
        }
        catch (Exception ex) when (IsRecoverableWebError(ex))
        {
            error.WriteLine($"Actio web UI worker could not acquire its runtime snapshot lock: {ex.Message}");
            return ExitCodes.ValidationError;
        }

        await using var acquiredRuntimeLock = runtimeLock;
        try
        {
            using var process = Process.GetCurrentProcess();
            await new ActioWebServer().RunAsync(
                new ActioWebOptions(
                    projectRoot,
                    actioHome,
                    url,
                    Background: true,
                    RuntimeIdentity: runtime.Identity,
                    WebInstanceId: worker.InstanceId,
                    ProcessId: Environment.ProcessId,
                    ProcessStartTimeUtcTicks: process.StartTime.ToUniversalTime().Ticks,
                    ControlToken: worker.ControlToken,
                    SessionId: worker.SessionId),
                async (binding, bindingCancellationToken) =>
                {
                    var metadata = await PublishWebWorkerBindingAsync(
                        processStore,
                        worker.InstanceId,
                        binding.ServerUrl,
                        TimeSpan.FromSeconds(3),
                        bindingCancellationToken);

                    processStore.AppendLog(
                        $"worker ready pid={Environment.ProcessId} instance={worker.InstanceId} runtime={runtime.Identity} url={binding.ServerUrl}");
                },
                cancellationToken);
            return ExitCodes.Success;
        }
        catch (Exception ex) when (IsRecoverableWebError(ex))
        {
            processStore.AppendLog($"worker failed: {ex.GetType().Name}: {ex.Message}");
            error.WriteLine($"Actio web UI failed: {ex.Message}");
            return ExitCodes.ValidationError;
        }
        finally
        {
            processStore.AppendLog(
                $"worker stopped pid={Environment.ProcessId} instance={worker.InstanceId} runtime={runtime.Identity}");
            processStore.DeleteIfOwned(worker.InstanceId);
        }
    }

    internal static WebWorkerContext? ReadWebWorkerContext(
        string projectRoot,
        string actioHome,
        string runtimeIdentity,
        TextWriter error)
    {
        var suppliedRuntimeIdentity = Environment.GetEnvironmentVariable(
            LocalWebServerLauncher.RuntimeIdentityEnvironmentVariable);
        var instanceId = Environment.GetEnvironmentVariable(
            LocalWebServerLauncher.InstanceIdEnvironmentVariable);
        var controlToken = Environment.GetEnvironmentVariable(
            LocalWebServerLauncher.ControlTokenEnvironmentVariable);
        var snapshotPath = Environment.GetEnvironmentVariable(
            LocalWebServerLauncher.SnapshotPathEnvironmentVariable);
        var sessionId = Environment.GetEnvironmentVariable(
            LocalWebServerLauncher.SessionIdEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(suppliedRuntimeIdentity) ||
            string.IsNullOrWhiteSpace(instanceId) ||
            string.IsNullOrWhiteSpace(controlToken) ||
            string.IsNullOrWhiteSpace(snapshotPath))
        {
            error.WriteLine(
                "Actio web --background is an internal managed worker mode and requires launcher metadata.");
            return null;
        }

        var expectedSnapshotPath = Path.Combine(
            Path.GetFullPath(actioHome),
            "web",
            "runtimes",
            runtimeIdentity);
        if (!string.Equals(suppliedRuntimeIdentity, runtimeIdentity, StringComparison.Ordinal) ||
            !IsSamePath(snapshotPath, expectedSnapshotPath) ||
            !IsSamePath(AppContext.BaseDirectory, expectedSnapshotPath))
        {
            error.WriteLine("Actio web worker runtime identity does not match its runtime snapshot.");
            return null;
        }

        if (sessionId is not null)
        {
            WebProjectSession expectedSession;
            try
            {
                expectedSession = WebProjectSession.Create(projectRoot, actioHome);
            }
            catch (Exception ex) when (IsRecoverableWebError(ex))
            {
                error.WriteLine($"Actio web worker project session could not be verified: {ex.Message}");
                return null;
            }

            if (!string.Equals(sessionId, expectedSession.Id, StringComparison.Ordinal))
            {
                error.WriteLine("Actio web worker project session identity is invalid.");
                return null;
            }
        }

        return new WebWorkerContext(instanceId, controlToken, sessionId);
    }

    internal static async Task<WebProcessMetadata> PublishWebWorkerBindingAsync(
        WebProcessMetadataStore processStore,
        string instanceId,
        string serverUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            var metadata = processStore.UpdateUrlIfOwned(instanceId, serverUrl);
            if (metadata is not null)
            {
                return metadata;
            }

            await Task.Delay(50, cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException(
            "Actio web worker could not publish its bound URL because its process metadata was not available or changed ownership.");
    }

    private static bool IsSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static bool IsRecoverableWebError(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or System.Text.Json.JsonException;
    }

    internal sealed record WebWorkerContext(
        string InstanceId,
        string ControlToken,
        string? SessionId);

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
