namespace Actio.Core.Workflows;

public sealed record WorkflowEventPayload(
    string EventName,
    string Source,
    string? Action = null,
    IReadOnlyDictionary<string, string>? Inputs = null,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = Inputs ?? new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        Properties ?? CreateDefaultProperties(EventName, Source, Action);

    public static WorkflowEventPayload Create(
        string eventName,
        string source,
        string? action = null,
        IReadOnlyDictionary<string, string>? inputs = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        return new WorkflowEventPayload(eventName, source, action, inputs, properties);
    }

    public string? GetValue(string path)
    {
        if (string.Equals(path, "event_name", StringComparison.Ordinal) ||
            string.Equals(path, "eventName", StringComparison.Ordinal))
        {
            return EventName;
        }

        if (string.Equals(path, "source", StringComparison.Ordinal))
        {
            return Source;
        }

        if (string.Equals(path, "action", StringComparison.Ordinal))
        {
            return Action;
        }

        const string inputPrefix = "inputs.";
        if (path.StartsWith(inputPrefix, StringComparison.Ordinal))
        {
            var inputName = path[inputPrefix.Length..];
            return Inputs.GetValueOrDefault(inputName);
        }

        return Properties.GetValueOrDefault(path);
    }

    private static IReadOnlyDictionary<string, string> CreateDefaultProperties(
        string eventName,
        string source,
        string? action)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["event_name"] = eventName,
            ["source"] = source
        };

        if (!string.IsNullOrWhiteSpace(action))
        {
            properties["action"] = action;
        }

        return properties;
    }
}
