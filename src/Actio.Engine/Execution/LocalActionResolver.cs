using System.Security.Cryptography;
using Actio.Core.Actions;
using Actio.Core.Workflows;
using Actio.Engine.Actions;

namespace Actio.Engine.Execution;

internal sealed class LocalActionResolver
{
    private readonly ActionParser _parser;
    private readonly IActionCache _cache;

    public LocalActionResolver(ActionParser parser, IActionCache cache)
    {
        _parser = parser;
        _cache = cache;
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

        var actionPathResult = ResolveActionPath(step.Uses, projectRoot);
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
                new LocalActionCacheRequest(step.Uses, actionPathResult.ActionPath!, contentHash),
                cancellationToken);

            return ActionResolutionResult.Resolved(
                parseResult.Action!,
                cacheEntry,
                BuildCommand(parseResult.Action!));
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching action '{step.Uses}'", ex)]);
        }
    }

    private static ActionPathResult ResolveActionPath(string uses, string projectRoot)
    {
        if (!ActionReference.IsSupportedLocalReference(uses))
        {
            return ActionPathResult.Failed([$"uses '{uses}' is not supported. Only local references starting with './' are supported."]);
        }

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
    IReadOnlyList<string> Errors)
{
    public static ActionResolutionResult Resolved(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, []);
    }

    public static ActionResolutionResult Failed(IReadOnlyList<string> errors)
    {
        return new ActionResolutionResult(false, null, null, null, errors);
    }
}
