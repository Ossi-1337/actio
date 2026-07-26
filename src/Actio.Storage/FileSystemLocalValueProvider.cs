using System.Collections;
using System.Security;

namespace Actio.Storage;

public sealed class FileSystemLocalValueProvider
{
    public const string ActioDirectoryName = ".actio";
    public const string VariablesFileName = "vars.env";
    public const string SecretsFileName = "secrets.env";
    public const string VariableEnvironmentPrefix = "ACTIO_VAR_";
    public const string SecretEnvironmentPrefix = "ACTIO_SECRET_";

    private readonly Func<IReadOnlyDictionary<string, string>> _getEnvironmentVariables;

    public FileSystemLocalValueProvider(Func<IReadOnlyDictionary<string, string>>? getEnvironmentVariables = null)
    {
        _getEnvironmentVariables = getEnvironmentVariables ?? GetProcessEnvironmentVariables;
    }

    public LocalValueProviderResult Load(string projectRoot)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        LoadFileValues(
            Path.Combine(projectRoot, ActioDirectoryName, VariablesFileName),
            "variable",
            variables,
            errors);
        LoadFileValues(
            Path.Combine(projectRoot, ActioDirectoryName, SecretsFileName),
            "secret",
            secrets,
            errors);
        LoadEnvironmentValues(VariableEnvironmentPrefix, "variable", variables, errors, rejectMultiline: false);
        LoadEnvironmentValues(SecretEnvironmentPrefix, "secret", secrets, errors, rejectMultiline: true);

        return errors.Count == 0
            ? LocalValueProviderResult.Loaded(new LocalWorkflowValues(variables, secrets))
            : LocalValueProviderResult.Failed(errors);
    }

    private static IReadOnlyDictionary<string, string> GetProcessEnvironmentVariables()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value?.ToString() ?? string.Empty;
            }
        }

        return values;
    }

    private static void LoadFileValues(
        string path,
        string valueKind,
        Dictionary<string, string> target,
        List<string> errors)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                AddFileValue(path, valueKind, index + 1, lines[index], target, errors);
            }
        }
        catch (Exception ex) when (IsRecoverableFileError(ex))
        {
            errors.Add($"{path} could not be read: {ex.Message}");
        }
    }

    private static void AddFileValue(
        string path,
        string valueKind,
        int lineNumber,
        string line,
        Dictionary<string, string> target,
        List<string> errors)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return;
        }

        if (trimmed.StartsWith("export ", StringComparison.Ordinal))
        {
            trimmed = trimmed["export ".Length..].TrimStart();
        }

        var separatorIndex = trimmed.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            errors.Add($"{path}:{lineNumber} must use NAME=value syntax.");
            return;
        }

        var name = trimmed[..separatorIndex].Trim();
        if (!IsValidName(name))
        {
            errors.Add($"{path}:{lineNumber} has invalid {valueKind} name '{name}'.");
            return;
        }

        target[name] = Unquote(trimmed[(separatorIndex + 1)..].Trim());
    }

    private void LoadEnvironmentValues(
        string prefix,
        string valueKind,
        Dictionary<string, string> target,
        List<string> errors,
        bool rejectMultiline)
    {
        foreach (var item in _getEnvironmentVariables()
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var name = item.Key[prefix.Length..];
            if (!IsValidName(name))
            {
                errors.Add($"Environment variable '{item.Key}' has invalid {valueKind} name '{name}'.");
                continue;
            }

            if (rejectMultiline && item.Value.IndexOfAny(['\r', '\n']) >= 0)
            {
                errors.Add($"Secret '{name}' must be a single-line value.");
                continue;
            }

            target[name] = item.Value;
        }
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2 &&
            value[0] == value[^1] &&
            value[0] is '"' or '\''
            ? value[1..^1]
            : value;
    }

    private static bool IsValidName(string value)
    {
        if (value.Length == 0 ||
            (!char.IsAsciiLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-');
    }

    private static bool IsRecoverableFileError(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or ArgumentException;
    }
}

public sealed record LocalWorkflowValues(
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyDictionary<string, string> Secrets)
{
    public static LocalWorkflowValues Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, string>());
}

public sealed record LocalValueProviderResult(
    bool Success,
    LocalWorkflowValues Values,
    IReadOnlyList<string> Errors)
{
    public static LocalValueProviderResult Loaded(LocalWorkflowValues values)
        => new(true, values, []);

    public static LocalValueProviderResult Failed(IReadOnlyList<string> errors)
        => new(false, LocalWorkflowValues.Empty, errors);
}
