using System.Text.Json.Nodes;

namespace Actio.Core.Expressions;

public sealed class ExpressionContextData
{
    private readonly IReadOnlyDictionary<string, ExpressionContextRoot> _roots;

    public ExpressionContextData(
        IEnumerable<ExpressionContextRoot> roots,
        string? workspaceRoot = null)
    {
        _roots = roots.ToDictionary(root => root.Name, StringComparer.Ordinal);
        WorkspaceRoot = workspaceRoot;
    }

    public string? WorkspaceRoot { get; }

    public ExpressionReferenceResolution Resolve(ExpressionReference reference)
    {
        if (!_roots.TryGetValue(reference.Root, out var root))
        {
            return ExpressionReferenceResolution.Failed($"Unsupported expression context '{reference.Root}'.");
        }

        if (!root.Available)
        {
            return ExpressionReferenceResolution.Failed(root.UnavailableMessage ?? $"Expression context '{reference.Root}' is not available in local Actio runs.");
        }

        var current = root.Value;
        var currentPath = root.Name;

        if (reference.Path.Count == 0)
        {
            return ExpressionReferenceResolution.Resolved(ExpressionValue.FromJsonNode(current));
        }

        foreach (var segment in reference.Path)
        {
            currentPath = $"{currentPath}.{segment}";

            if (current is JsonObject currentObject)
            {
                if (currentObject.TryGetPropertyValue(segment, out current))
                {
                    continue;
                }

                if (root.MissingPropertyMessages.TryGetValue(currentPath, out var missingPropertyMessage))
                {
                    return ExpressionReferenceResolution.Failed(missingPropertyMessage);
                }

                return root.AllowMissingProperties
                    ? ExpressionReferenceResolution.Resolved(ExpressionValue.Null)
                    : ExpressionReferenceResolution.Failed($"Expression context '{currentPath}' is not available in local Actio runs.");
            }

            return ExpressionReferenceResolution.Failed($"Expression context '{currentPath}' is not available in local Actio runs.");
        }

        return ExpressionReferenceResolution.Resolved(ExpressionValue.FromJsonNode(current));
    }

    public IReadOnlyDictionary<string, JsonNode?> ToSafeJson()
    {
        var values = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var root in _roots.Values.Where(root => root.IncludeInSafeSnapshot && root.Available))
        {
            values[root.Name] = root.Value?.DeepClone();
        }

        return values;
    }

    public static JsonObject FromStrings(IReadOnlyDictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var item in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            json[item.Key] = item.Value;
        }

        return json;
    }
}

public sealed record ExpressionContextRoot(
    string Name,
    JsonNode? Value,
    bool AllowMissingProperties,
    bool IncludeInSafeSnapshot,
    bool Available,
    string? UnavailableMessage,
    IReadOnlyDictionary<string, string> MissingPropertyMessages)
{
    public static ExpressionContextRoot AvailableRoot(
        string name,
        JsonNode? value,
        bool allowMissingProperties = false,
        bool includeInSafeSnapshot = true,
        IReadOnlyDictionary<string, string>? missingPropertyMessages = null)
    {
        return new ExpressionContextRoot(
            name,
            value,
            allowMissingProperties,
            includeInSafeSnapshot,
            true,
            null,
            missingPropertyMessages ?? new Dictionary<string, string>());
    }

    public static ExpressionContextRoot UnavailableRoot(
        string name,
        string message,
        bool includeInSafeSnapshot = false)
    {
        return new ExpressionContextRoot(
            name,
            null,
            false,
            includeInSafeSnapshot,
            false,
            message,
            new Dictionary<string, string>());
    }
}
