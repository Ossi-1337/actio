using System.Security.Cryptography;
using System.Text;
using Actio.Core.Actions;
using Actio.Core.Workflows;
using Actio.Engine.Actions;

namespace Actio.Engine.Execution;

internal sealed class ActionResolver
{
    private const string ActionContainerPath = "/actio/action";
    private const string CheckoutShimCommand = "printf '%s\\n' 'Actio checkout shim: workspace is already available at /workspace.'";

    private readonly ActionParser _parser;
    private readonly IActionCache _cache;
    private readonly IGitHubActionSourceProvider _githubActionSourceProvider;

    public ActionResolver(
        ActionParser parser,
        IActionCache cache,
        IGitHubActionSourceProvider githubActionSourceProvider)
    {
        _parser = parser;
        _cache = cache;
        _githubActionSourceProvider = githubActionSourceProvider;
    }

    public async Task<ActionResolutionResult> ResolveAsync(
        WorkflowStep step,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        if (step.Uses is null)
        {
            return ActionResolutionResult.Failed(["Step does not define uses."]);
        }

        if (!ActionReference.TryParse(step.Uses, out var reference))
        {
            return ActionResolutionResult.Failed([$"uses '{step.Uses}' is not supported. Supported formats are './...', 'docker://...', and 'owner/repo[/path]@ref'."]);
        }

        return reference!.Kind switch
        {
            ActionReferenceKind.Local => await ResolveLocalActionAsync(step, projectRoot, cancellationToken),
            ActionReferenceKind.DockerImage => await ResolveDockerImageActionAsync(step, reference, cancellationToken),
            ActionReferenceKind.GitHubRepository => await ResolveGitHubActionAsync(step, reference, projectRoot, cancellationToken),
            _ => ActionResolutionResult.Failed([$"uses '{step.Uses}' is not supported."])
        };
    }

    private async Task<ActionResolutionResult> ResolveLocalActionAsync(
        WorkflowStep step,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var uses = step.Uses!;
        var actionPathResult = ResolveLocalActionPath(uses, projectRoot);
        if (!actionPathResult.Success)
        {
            return ActionResolutionResult.Failed(actionPathResult.Errors);
        }

        var parseResult = _parser.ParseFile(actionPathResult.ActionPath!);
        if (!parseResult.Success)
        {
            return ActionResolutionResult.Failed(parseResult.Errors);
        }

        var inputBinding = ActionInputBinder.Bind(parseResult.Action!, step.With);
        if (!inputBinding.Success)
        {
            return ActionResolutionResult.Failed(inputBinding.Errors);
        }

        string? compositeCommand = null;
        if (string.Equals(parseResult.Action!.Runtime, ActionRuntime.Composite, StringComparison.Ordinal))
        {
            var command = BuildCommand(parseResult.Action, inputBinding.Inputs, projectRoot);
            if (!command.Success)
            {
                return ActionResolutionResult.Failed(command.Errors);
            }

            compositeCommand = command.Value;
        }

        try
        {
            var contentHash = await ComputeContentHashAsync(actionPathResult.ActionPath!, cancellationToken);
            var cacheEntry = await _cache.GetOrAddLocalActionAsync(
                new LocalActionCacheRequest(uses, actionPathResult.ActionPath!, contentHash),
                cancellationToken);

            return ResolveParsedAction(
                parseResult.Action!,
                cacheEntry,
                inputBinding,
                Path.GetDirectoryName(actionPathResult.ActionPath!)!,
                projectRoot,
                compositeCommand);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching action '{uses}'", ex)]);
        }
    }

    private async Task<ActionResolutionResult> ResolveDockerImageActionAsync(
        WorkflowStep step,
        ActionReference reference,
        CancellationToken cancellationToken)
    {
        var uses = step.Uses!;
        var dockerOptions = DockerImageActionOptions.FromInputs(step.With);
        if (!dockerOptions.Success)
        {
            return ActionResolutionResult.Failed(dockerOptions.Errors);
        }

        try
        {
            var cacheEntry = await _cache.GetOrAddDockerImageActionAsync(
                new DockerImageActionCacheRequest(uses, reference.Target, reference.IsPinned, reference.MutablePart),
                cancellationToken);

            return ActionResolutionResult.ResolvedDockerImage(
                uses,
                reference.Target,
                cacheEntry,
                ActionInputBinder.CreateEnvironment(step.With),
                dockerOptions.EntryPoint,
                dockerOptions.Arguments);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching action '{uses}'", ex)]);
        }
    }

    private async Task<ActionResolutionResult> ResolveGitHubActionAsync(
        WorkflowStep step,
        ActionReference reference,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var uses = step.Uses!;
        if (!reference.TryGetGitHubAction(out var githubAction))
        {
            return ActionResolutionResult.Failed([$"uses '{uses}' is not a valid GitHub action reference."]);
        }

        if (IsCheckoutShim(githubAction!))
        {
            if (step.With.Count > 0)
            {
                return ActionResolutionResult.Failed(["actions/checkout@v4 with inputs is not supported by the Actio checkout shim yet."]);
            }

            return ActionResolutionResult.ResolvedBuiltInAction(CheckoutShimCommand);
        }

        var sourceResult = await _githubActionSourceProvider.GetGitHubActionSourceAsync(
            new GitHubActionSourceRequest(
                uses,
                githubAction!.Owner,
                githubAction.Repository,
                githubAction.ActionPath,
                githubAction.Ref,
                reference.IsPinned,
                reference.MutablePart),
            cancellationToken);

        if (!sourceResult.Success)
        {
            return ActionResolutionResult.Failed(sourceResult.Errors);
        }

        var parseResult = _parser.ParseFile(sourceResult.ActionFilePath!);
        if (!parseResult.Success)
        {
            return ActionResolutionResult.Failed(parseResult.Errors);
        }

        var inputBinding = ActionInputBinder.Bind(parseResult.Action!, step.With);
        if (!inputBinding.Success)
        {
            return ActionResolutionResult.Failed(inputBinding.Errors);
        }

        return ResolveParsedAction(
            parseResult.Action!,
            sourceResult.CacheEntry!,
            inputBinding,
            sourceResult.ActionDirectory!,
            projectRoot);
    }

    private static bool IsCheckoutShim(GitHubActionReference action)
    {
        return string.Equals(action.Owner, "actions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Repository, "checkout", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(action.ActionPath) &&
            string.Equals(action.Ref, "v4", StringComparison.Ordinal);
    }

    private static ActionPathResult ResolveLocalActionPath(string uses, string projectRoot)
    {
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var candidate = Path.GetFullPath(Path.Combine(fullProjectRoot, uses));
        if (!IsUnderRoot(candidate, fullProjectRoot))
        {
            return ActionPathResult.Failed([$"uses '{uses}' must stay inside the project root."]);
        }

        if (File.Exists(candidate))
        {
            return IsActionFile(candidate)
                ? ActionPathResult.Resolved(candidate)
                : ActionPathResult.Failed([$"uses '{uses}' must point to an action.yml or action.yaml file."]);
        }

        if (Directory.Exists(candidate))
        {
            var ymlPath = Path.Combine(candidate, "action.yml");
            if (File.Exists(ymlPath))
            {
                return ActionPathResult.Resolved(ymlPath);
            }

            var yamlPath = Path.Combine(candidate, "action.yaml");
            if (File.Exists(yamlPath))
            {
                return ActionPathResult.Resolved(yamlPath);
            }
        }

        return ActionPathResult.Failed([$"uses '{uses}' could not be resolved to a local action.yml or action.yaml file."]);
    }

    private static ActionInputInterpolationResult BuildCommand(
        ActionDocument action,
        IReadOnlyDictionary<string, string> inputs,
        string projectRoot)
    {
        var commands = new List<string>();
        var errors = new List<string>();

        foreach (var step in action.Steps)
        {
            var interpolation = ActionInputBinder.InterpolateInputExpressions(step.Run, inputs, projectRoot);
            if (interpolation.Success)
            {
                commands.Add(interpolation.Value);
            }
            else
            {
                errors.AddRange(interpolation.Errors);
            }
        }

        return errors.Count == 0
            ? ActionInputInterpolationResult.Resolved(string.Join(Environment.NewLine, commands))
            : ActionInputInterpolationResult.Failed(errors);
    }

    private static ActionResolutionResult ResolveParsedAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        ActionInputBindingResult inputBinding,
        string actionDirectory,
        string projectRoot,
        string? compositeCommand = null)
    {
        var environment = MergeEnvironment(
            inputBinding.Environment,
            CreateActionPathEnvironment());
        var actionMount = new StepExecutionMount(actionDirectory, ActionContainerPath, ReadOnly: true);

        if (string.Equals(action.Runtime, ActionRuntime.Node20, StringComparison.Ordinal))
        {
            return ActionResolutionResult.ResolvedJavaScriptAction(
                action,
                cacheEntry,
                FormatJavaScriptCommand(action.Main!),
                ActionContainerPath,
                action.Main!,
                action.Pre,
                action.Post,
                environment,
                [actionMount]);
        }

        if (compositeCommand is null)
        {
            var command = BuildCommand(action, inputBinding.Inputs, projectRoot);
            if (!command.Success)
            {
                return ActionResolutionResult.Failed(command.Errors);
            }

            compositeCommand = command.Value;
        }

        return ActionResolutionResult.ResolvedCompositeAction(
            action,
            cacheEntry,
            compositeCommand,
            environment,
            [actionMount]);
    }

    private static IReadOnlyDictionary<string, string> CreateActionPathEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ACTIO_ACTION_PATH"] = ActionContainerPath,
            ["GITHUB_ACTION_PATH"] = ActionContainerPath
        };
    }

    private static string FormatJavaScriptCommand(string main)
        => $"node {ToActionContainerPath(main)}";

    private static string ToActionContainerPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return $"{ActionContainerPath}/{normalized}";
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var environment = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var item in second)
        {
            environment[item.Key] = item.Value;
        }

        return environment;
    }

    private static async Task<string> ComputeContentHashAsync(
        string actionPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(actionPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsActionFile(string path)
    {
        return string.Equals(Path.GetFileName(path), "action.yml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(path), "action.yaml", StringComparison.OrdinalIgnoreCase);
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

    private sealed record ActionPathResult(
        bool Success,
        string? ActionPath,
        IReadOnlyList<string> Errors)
    {
        public static ActionPathResult Resolved(string actionPath)
            => new(true, actionPath, []);

        public static ActionPathResult Failed(IReadOnlyList<string> errors)
            => new(false, null, errors);
    }

    private sealed record DockerImageActionOptions(
        bool Success,
        string? EntryPoint,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<string> Errors)
    {
        public static DockerImageActionOptions FromInputs(IReadOnlyDictionary<string, string> inputs)
        {
            var entryPoint = inputs.TryGetValue("entrypoint", out var value)
                ? value
                : null;

            if (!inputs.TryGetValue("args", out var args))
            {
                return Resolved(entryPoint, []);
            }

            var arguments = SplitArguments(args);
            return arguments.Success
                ? Resolved(entryPoint, arguments.Arguments)
                : Failed(arguments.Errors);
        }

        private static DockerImageActionOptions Resolved(
            string? entryPoint,
            IReadOnlyList<string> arguments)
            => new(true, entryPoint, arguments, []);

        private static DockerImageActionOptions Failed(IReadOnlyList<string> errors)
            => new(false, null, [], errors);
    }

    private sealed record DockerImageActionArgumentResult(
        bool Success,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<string> Errors)
    {
        public static DockerImageActionArgumentResult Resolved(IReadOnlyList<string> arguments)
            => new(true, arguments, []);

        public static DockerImageActionArgumentResult Failed(IReadOnlyList<string> errors)
            => new(false, [], errors);
    }

    private static DockerImageActionArgumentResult SplitArguments(string value)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var tokenStarted = false;
        char? quote = null;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (quote is not null &&
                character == '\\' &&
                index + 1 < value.Length &&
                IsEscapedQuotedCharacter(value[index + 1], quote.Value))
            {
                current.Append(value[index + 1]);
                tokenStarted = true;
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                if (quote is null)
                {
                    quote = character;
                    tokenStarted = true;
                    continue;
                }

                if (quote == character)
                {
                    quote = null;
                    continue;
                }
            }

            if (quote is null && char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(character);
            tokenStarted = true;
        }

        if (quote is not null)
        {
            return DockerImageActionArgumentResult.Failed(["docker image action with.args contains an unterminated quote."]);
        }

        if (tokenStarted)
        {
            arguments.Add(current.ToString());
        }

        return DockerImageActionArgumentResult.Resolved(arguments);
    }

    private static bool IsEscapedQuotedCharacter(char character, char quote)
        => character == quote || character == '\\';
}

internal sealed record ActionResolutionResult(
    bool Success,
    ActionDocument? Action,
    ActionCacheEntry? CacheEntry,
    string? Command,
    string? DockerImage,
    string? DockerEntryPoint,
    IReadOnlyList<string> DockerArguments,
    string? JavaScriptActionPath,
    string? JavaScriptMain,
    string? JavaScriptPre,
    string? JavaScriptPost,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<StepExecutionMount> AdditionalMounts,
    IReadOnlyList<string> Errors)
{
    public bool IsDockerImageAction => DockerImage is not null;

    public bool IsJavaScriptAction => JavaScriptMain is not null;

    public static ActionResolutionResult ResolvedCompositeAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> additionalMounts)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, null, null, [], null, null, null, null, environment, additionalMounts, []);
    }

    public static ActionResolutionResult ResolvedJavaScriptAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command,
        string actionPath,
        string main,
        string? pre,
        string? post,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> additionalMounts)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, null, null, [], actionPath, main, pre, post, environment, additionalMounts, []);
    }

    public static ActionResolutionResult ResolvedBuiltInAction(string command)
    {
        return new ActionResolutionResult(true, null, null, command, null, null, [], null, null, null, null, new Dictionary<string, string>(), [], []);
    }

    public static ActionResolutionResult ResolvedDockerImage(
        string command,
        string dockerImage,
        ActionCacheEntry cacheEntry,
        IReadOnlyDictionary<string, string> environment,
        string? dockerEntryPoint,
        IReadOnlyList<string> dockerArguments)
    {
        return new ActionResolutionResult(
            true,
            null,
            cacheEntry,
            command,
            dockerImage,
            dockerEntryPoint,
            dockerArguments,
            null,
            null,
            null,
            null,
            environment,
            [],
            []);
    }

    public static ActionResolutionResult Failed(IReadOnlyList<string> errors)
    {
        return new ActionResolutionResult(false, null, null, null, null, null, [], null, null, null, null, new Dictionary<string, string>(), [], errors);
    }
}
