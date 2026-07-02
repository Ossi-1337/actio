using Actio.Core.Actions;

namespace Actio.Engine.Setup;

internal static class SetupActionResolver
{
    private static readonly IReadOnlySet<string> NodeInputs = new HashSet<string>(StringComparer.Ordinal) { "node-version" };
    private static readonly IReadOnlySet<string> PythonInputs = new HashSet<string>(StringComparer.Ordinal) { "python-version" };
    private static readonly IReadOnlySet<string> JavaInputs = new HashSet<string>(StringComparer.Ordinal) { "java-version", "distribution" };
    private static readonly IReadOnlySet<string> GoInputs = new HashSet<string>(StringComparer.Ordinal) { "go-version" };
    private static readonly IReadOnlySet<string> DotNetInputs = new HashSet<string>(StringComparer.Ordinal) { "dotnet-version" };

    public static SetupActionResolution Resolve(
        string? uses,
        IReadOnlyDictionary<string, string> with)
    {
        if (uses is null ||
            !ActionReference.TryParse(uses, out var reference) ||
            !reference!.TryGetGitHubAction(out var githubAction) ||
            !TryGetSetupActionKind(githubAction!, out var kind))
        {
            return SetupActionResolution.NotSetupAction;
        }

        var actionName = $"actions/{githubAction!.Repository}";
        var errors = new List<string>();
        foreach (var key in with.Keys)
        {
            if (!GetSetupActionInputs(kind).Contains(key))
            {
                errors.Add(FormatUnsupportedInput(actionName, key));
            }
        }

        var versionInputName = GetVersionInput(kind);
        var requestedVersion = ReadOptionalInput(with, versionInputName);
        var versionMatchPattern = requestedVersion is null
            ? null
            : CreateVersionMatchPattern(actionName, requestedVersion, errors);
        var distribution = kind == SetupActionKind.Java
            ? ReadOptionalInput(with, "distribution")
            : null;

        return errors.Count == 0
            ? SetupActionResolution.Resolved(new SetupAction(kind, actionName, requestedVersion, versionMatchPattern, distribution))
            : SetupActionResolution.Failed(errors);
    }

    private static bool TryGetSetupActionKind(GitHubActionReference action, out SetupActionKind kind)
    {
        kind = default;
        if (!string.Equals(action.Owner, "actions", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(action.ActionPath))
        {
            return false;
        }

        if (string.Equals(action.Repository, "setup-node", StringComparison.OrdinalIgnoreCase))
        {
            kind = SetupActionKind.Node;
            return true;
        }

        if (string.Equals(action.Repository, "setup-python", StringComparison.OrdinalIgnoreCase))
        {
            kind = SetupActionKind.Python;
            return true;
        }

        if (string.Equals(action.Repository, "setup-java", StringComparison.OrdinalIgnoreCase))
        {
            kind = SetupActionKind.Java;
            return true;
        }

        if (string.Equals(action.Repository, "setup-go", StringComparison.OrdinalIgnoreCase))
        {
            kind = SetupActionKind.Go;
            return true;
        }

        if (string.Equals(action.Repository, "setup-dotnet", StringComparison.OrdinalIgnoreCase))
        {
            kind = SetupActionKind.DotNet;
            return true;
        }

        return false;
    }

    private static IReadOnlySet<string> GetSetupActionInputs(SetupActionKind kind)
    {
        return kind switch
        {
            SetupActionKind.Node => NodeInputs,
            SetupActionKind.Python => PythonInputs,
            SetupActionKind.Java => JavaInputs,
            SetupActionKind.Go => GoInputs,
            SetupActionKind.DotNet => DotNetInputs,
            _ => throw new InvalidOperationException($"Unsupported setup action kind '{kind}'.")
        };
    }

    private static string GetVersionInput(SetupActionKind kind)
    {
        return kind switch
        {
            SetupActionKind.Node => "node-version",
            SetupActionKind.Python => "python-version",
            SetupActionKind.Java => "java-version",
            SetupActionKind.Go => "go-version",
            SetupActionKind.DotNet => "dotnet-version",
            _ => throw new InvalidOperationException($"Unsupported setup action kind '{kind}'.")
        };
    }

    private static string FormatUnsupportedInput(string actionName, string inputName)
    {
        return $"{actionName} with.{inputName} is not supported by the Actio setup shim. The shim does not install tools, read version files, configure registries, authenticate package feeds, or manage dependency caches. Use a runner image with the required runtime and actions/cache for dependency caching.";
    }

    private static string? CreateVersionMatchPattern(
        string actionName,
        string requestedVersion,
        List<string> errors)
    {
        var normalized = requestedVersion.StartsWith('v') || requestedVersion.StartsWith('V')
            ? requestedVersion[1..]
            : requestedVersion;
        var parts = normalized.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add(FormatUnsupportedVersion(actionName, requestedVersion));
            return null;
        }

        var wildcardIndex = Array.FindIndex(parts, part => string.Equals(part, "x", StringComparison.OrdinalIgnoreCase));
        if (wildcardIndex >= 0 && wildcardIndex != parts.Length - 1)
        {
            errors.Add(FormatUnsupportedVersion(actionName, requestedVersion));
            return null;
        }

        if (parts[0].Equals("x", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(FormatUnsupportedVersion(actionName, requestedVersion));
            return null;
        }

        foreach (var part in parts.Where(part => !part.Equals("x", StringComparison.OrdinalIgnoreCase)))
        {
            if (!part.All(char.IsDigit))
            {
                errors.Add(FormatUnsupportedVersion(actionName, requestedVersion));
                return null;
            }
        }

        var concreteParts = wildcardIndex >= 0
            ? parts.Take(wildcardIndex).ToArray()
            : parts;
        var prefix = string.Join(".", concreteParts);
        return parts.Length == 3 && wildcardIndex < 0
            ? prefix
            : $"{prefix}|{prefix}.*";
    }

    private static string FormatUnsupportedVersion(string actionName, string requestedVersion)
    {
        return $"{actionName} version '{requestedVersion}' is not supported by the Actio setup shim. Use a major, major.minor, major.minor.patch, optional leading 'v', or trailing 'x' wildcard version such as '20', '20.11', '20.11.1', or '20.11.x'.";
    }

    private static string? ReadOptionalInput(
        IReadOnlyDictionary<string, string> with,
        string name)
    {
        return with.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }
}
