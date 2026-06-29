using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Actio.Core.Expressions;

public static class ExpressionBuiltInFunctions
{
    public static bool TryEvaluate(
        ExpressionFunctionCall function,
        IReadOnlyList<ExpressionValue> arguments,
        ExpressionEvaluationContext context,
        out ExpressionEvaluationResult result)
    {
        if (string.Equals(function.Name, "contains", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateContains(arguments);
            return true;
        }

        if (string.Equals(function.Name, "startsWith", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateStringPredicate(function.Name, arguments, (value, search) => value.StartsWith(search, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        if (string.Equals(function.Name, "endsWith", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateStringPredicate(function.Name, arguments, (value, search) => value.EndsWith(search, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        if (string.Equals(function.Name, "format", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateFormat(arguments);
            return true;
        }

        if (string.Equals(function.Name, "join", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateJoin(arguments);
            return true;
        }

        if (string.Equals(function.Name, "toJSON", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateToJson(arguments);
            return true;
        }

        if (string.Equals(function.Name, "fromJSON", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateFromJson(arguments);
            return true;
        }

        if (string.Equals(function.Name, "hashFiles", StringComparison.OrdinalIgnoreCase))
        {
            result = EvaluateHashFiles(arguments, context.WorkspaceRoot);
            return true;
        }

        result = ExpressionEvaluationResult.Failed([]);
        return false;
    }

    private static ExpressionEvaluationResult EvaluateContains(IReadOnlyList<ExpressionValue> arguments)
    {
        var arity = ValidateArity("contains", arguments, 2, 2);
        if (arity is not null)
        {
            return arity;
        }

        if (TryGetJsonArray(arguments[0], out var array))
        {
            return ExpressionEvaluationResult.Resolved(
                ExpressionValue.FromBoolean(
                    array.Any(item => string.Equals(
                        ToExpressionValue(item).AsString(),
                        arguments[1].AsString(),
                        StringComparison.OrdinalIgnoreCase))));
        }

        return ExpressionEvaluationResult.Resolved(
            ExpressionValue.FromBoolean(
                arguments[0].AsString().Contains(arguments[1].AsString(), StringComparison.OrdinalIgnoreCase)));
    }

    private static ExpressionEvaluationResult EvaluateStringPredicate(
        string functionName,
        IReadOnlyList<ExpressionValue> arguments,
        Func<string, string, bool> predicate)
    {
        var arity = ValidateArity(functionName, arguments, 2, 2);
        if (arity is not null)
        {
            return arity;
        }

        return ExpressionEvaluationResult.Resolved(
            ExpressionValue.FromBoolean(predicate(arguments[0].AsString(), arguments[1].AsString())));
    }

    private static ExpressionEvaluationResult EvaluateFormat(IReadOnlyList<ExpressionValue> arguments)
    {
        var arity = ValidateArity("format", arguments, 1, int.MaxValue);
        if (arity is not null)
        {
            return arity;
        }

        try
        {
            return ExpressionEvaluationResult.Resolved(
                ExpressionValue.FromString(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        arguments[0].AsString(),
                        arguments.Skip(1).Select(argument => argument.AsString()).Cast<object>().ToArray())));
        }
        catch (FormatException ex)
        {
            return ExpressionEvaluationResult.Failed([$"format() failed: {ex.Message}"]);
        }
    }

    private static ExpressionEvaluationResult EvaluateJoin(IReadOnlyList<ExpressionValue> arguments)
    {
        var arity = ValidateArity("join", arguments, 1, 2);
        if (arity is not null)
        {
            return arity;
        }

        var separator = arguments.Count == 2 ? arguments[1].AsString() : ",";
        var values = TryGetJsonArray(arguments[0], out var array)
            ? array.Select(item => ToExpressionValue(item).AsString())
            : [arguments[0].AsString()];

        return ExpressionEvaluationResult.Resolved(ExpressionValue.FromString(string.Join(separator, values)));
    }

    private static ExpressionEvaluationResult EvaluateToJson(IReadOnlyList<ExpressionValue> arguments)
    {
        var arity = ValidateArity("toJSON", arguments, 1, 1);
        if (arity is not null)
        {
            return arity;
        }

        return ExpressionEvaluationResult.Resolved(
            ExpressionValue.FromString(arguments[0].ToJsonNode()?.ToJsonString(ExpressionJson.SerializerOptions) ?? "null"));
    }

    private static ExpressionEvaluationResult EvaluateFromJson(IReadOnlyList<ExpressionValue> arguments)
    {
        var arity = ValidateArity("fromJSON", arguments, 1, 1);
        if (arity is not null)
        {
            return arity;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments[0].AsString());
            return ExpressionEvaluationResult.Resolved(ExpressionValue.FromJsonElement(document.RootElement));
        }
        catch (JsonException ex)
        {
            return ExpressionEvaluationResult.Failed([$"fromJSON() failed: {ex.Message}"]);
        }
    }

    private static ExpressionEvaluationResult EvaluateHashFiles(
        IReadOnlyList<ExpressionValue> arguments,
        string? workspaceRoot)
    {
        var arity = ValidateArity("hashFiles", arguments, 1, int.MaxValue);
        if (arity is not null)
        {
            return arity;
        }

        if (workspaceRoot is null)
        {
            return ExpressionEvaluationResult.Failed(["hashFiles() requires a workspace root."]);
        }

        try
        {
            return ExpressionEvaluationResult.Resolved(
                ExpressionValue.FromString(HashFiles(workspaceRoot, arguments.Select(argument => argument.AsString()))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ExpressionEvaluationResult.Failed([$"hashFiles() failed: {ex.Message}"]);
        }
    }

    private static string HashFiles(string workspaceRoot, IEnumerable<string> patterns)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var files = new SortedSet<string>(comparison);

        foreach (var pattern in patterns)
        {
            UpdateMatchedFiles(root, pattern, files);
        }

        if (files.Count == 0)
        {
            return string.Empty;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in files)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var fileHash = SHA256.HashData(File.ReadAllBytes(fullPath));
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/')));
            hash.AppendData([0]);
            hash.AppendData(fileHash);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void UpdateMatchedFiles(
        string root,
        string pattern,
        SortedSet<string> files)
    {
        var include = true;
        var normalizedPattern = NormalizePattern(pattern);
        if (normalizedPattern.StartsWith('!'))
        {
            include = false;
            normalizedPattern = normalizedPattern[1..];
        }

        if (!IsSafePattern(normalizedPattern))
        {
            throw new ArgumentException($"hashFiles() pattern '{pattern}' must be relative and stay inside the workspace.");
        }

        var regex = GlobRegex(normalizedPattern);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!regex.IsMatch(relativePath))
            {
                continue;
            }

            if (include)
            {
                files.Add(relativePath);
            }
            else
            {
                files.Remove(relativePath);
            }
        }
    }

    private static Regex GlobRegex(string pattern)
    {
        var builder = new StringBuilder("^");

        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        index += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        index++;
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            builder.Append(current == '?' ? "[^/]" : Regex.Escape(current.ToString()));
        }

        builder.Append('$');
        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows())
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(builder.ToString(), options);
    }

    private static string NormalizePattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool IsSafePattern(string pattern)
    {
        return !string.IsNullOrWhiteSpace(pattern) &&
            !Path.IsPathRooted(pattern) &&
            !pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..");
    }

    private static bool TryGetJsonArray(ExpressionValue value, out JsonArray array)
    {
        if (value.Kind == ExpressionValueKind.Json && value.Value is JsonArray jsonArray)
        {
            array = jsonArray;
            return true;
        }

        array = [];
        return false;
    }

    private static ExpressionValue ToExpressionValue(JsonNode? node)
    {
        if (node is null)
        {
            return ExpressionValue.Null;
        }

        using var document = JsonDocument.Parse(node.ToJsonString(ExpressionJson.SerializerOptions));
        return ExpressionValue.FromJsonElement(document.RootElement);
    }

    private static ExpressionEvaluationResult? ValidateArity(
        string functionName,
        IReadOnlyList<ExpressionValue> arguments,
        int min,
        int max)
    {
        if (arguments.Count >= min && arguments.Count <= max)
        {
            return null;
        }

        var expected = max == int.MaxValue
            ? $"at least {min.ToString(CultureInfo.InvariantCulture)}"
            : min == max
                ? min.ToString(CultureInfo.InvariantCulture)
                : $"{min}-{max}";
        return ExpressionEvaluationResult.Failed([$"{functionName}() expects {expected} argument(s), but got {arguments.Count}."]);
    }
}
