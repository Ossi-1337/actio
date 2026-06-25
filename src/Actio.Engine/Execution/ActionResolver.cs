using System.Security.Cryptography;
using Actio.Core.Actions;
using Actio.Core.Workflows;
using Actio.Engine.Actions;

namespace Actio.Engine.Execution;

internal sealed class ActionResolver
{
    private const string GitHubActionContainerPath = "/actio/action";

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
            ActionReferenceKind.GitHubRepository => await ResolveGitHubActionAsync(step, reference, cancellationToken),
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

        try
        {
            var contentHash = await ComputeContentHashAsync(actionPathResult.ActionPath!, cancellationToken);
            var cacheEntry = await _cache.GetOrAddLocalActionAsync(
                new LocalActionCacheRequest(uses, actionPathResult.ActionPath!, contentHash),
                cancellationToken);

            return ActionResolutionResult.ResolvedLocalAction(
                parseResult.Action!,
                cacheEntry,
                BuildCommand(parseResult.Action!));
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

        try
        {
            var cacheEntry = await _cache.GetOrAddDockerImageActionAsync(
                new DockerImageActionCacheRequest(uses, reference.Target, reference.IsPinned, reference.MutablePart),
                cancellationToken);

            return ActionResolutionResult.ResolvedDockerImage(uses, reference.Target, cacheEntry);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching action '{uses}'", ex)]);
        }
    }

    private async Task<ActionResolutionResult> ResolveGitHubActionAsync(
        WorkflowStep step,
        ActionReference reference,
        CancellationToken cancellationToken)
    {
        var uses = step.Uses!;
        if (!reference.TryGetGitHubAction(out var githubAction))
        {
            return ActionResolutionResult.Failed([$"uses '{uses}' is not a valid GitHub action reference."]);
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

        return ActionResolutionResult.ResolvedGitHubAction(
            parseResult.Action!,
            sourceResult.CacheEntry!,
            BuildCommand(parseResult.Action!),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ACTIO_ACTION_PATH"] = GitHubActionContainerPath,
                ["GITHUB_ACTION_PATH"] = GitHubActionContainerPath
            },
            [new StepExecutionMount(sourceResult.ActionDirectory!, GitHubActionContainerPath, ReadOnly: true)]);
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

    private static string BuildCommand(ActionDocument action)
    {
        return string.Join(Environment.NewLine, action.Steps.Select(step => step.Run));
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
}

internal sealed record ActionResolutionResult(
    bool Success,
    ActionDocument? Action,
    ActionCacheEntry? CacheEntry,
    string? Command,
    string? DockerImage,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<StepExecutionMount> AdditionalMounts,
    IReadOnlyList<string> Errors)
{
    public bool IsDockerImageAction => DockerImage is not null;

    public static ActionResolutionResult ResolvedLocalAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, null, new Dictionary<string, string>(), [], []);
    }

    public static ActionResolutionResult ResolvedGitHubAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> additionalMounts)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, null, environment, additionalMounts, []);
    }

    public static ActionResolutionResult ResolvedDockerImage(
        string command,
        string dockerImage,
        ActionCacheEntry cacheEntry)
    {
        return new ActionResolutionResult(true, null, cacheEntry, command, dockerImage, new Dictionary<string, string>(), [], []);
    }

    public static ActionResolutionResult Failed(IReadOnlyList<string> errors)
    {
        return new ActionResolutionResult(false, null, null, null, null, new Dictionary<string, string>(), [], errors);
    }
}
