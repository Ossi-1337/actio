using System.Text.RegularExpressions;

namespace Actio.Core.Workflows;

public enum WorkflowConditionExpressionKind
{
    NeedsOutput,
    Input,
    EventPayload
}

public sealed partial record WorkflowConditionExpression(
    WorkflowConditionExpressionKind Kind,
    string? ReferencedJob,
    string Name,
    string ExpectedValue)
{
    public static bool TryParse(string expression, out WorkflowConditionExpression? condition)
    {
        var needsMatch = NeedsOutputConditionRegex().Match(expression);
        if (needsMatch.Success)
        {
            condition = new WorkflowConditionExpression(
                WorkflowConditionExpressionKind.NeedsOutput,
                needsMatch.Groups["job"].Value,
                needsMatch.Groups["output"].Value,
                needsMatch.Groups["value"].Value);
            return true;
        }

        var inputMatch = InputConditionRegex().Match(expression);
        if (inputMatch.Success)
        {
            condition = new WorkflowConditionExpression(
                WorkflowConditionExpressionKind.Input,
                null,
                inputMatch.Groups["input"].Value,
                inputMatch.Groups["value"].Value);
            return true;
        }

        var eventPayloadMatch = EventPayloadConditionRegex().Match(expression);
        if (eventPayloadMatch.Success)
        {
            condition = new WorkflowConditionExpression(
                WorkflowConditionExpressionKind.EventPayload,
                null,
                eventPayloadMatch.Groups["path"].Value,
                eventPayloadMatch.Groups["value"].Value);
            return true;
        }

        condition = null;
        return false;
    }

    [GeneratedRegex("^\\$\\{\\{\\s*needs\\.(?<job>[A-Za-z0-9_-]+)\\.outputs\\.(?<output>[A-Za-z0-9_-]+)\\s*==\\s*'(?<value>[^']*)'\\s*\\}\\}$")]
    private static partial Regex NeedsOutputConditionRegex();

    [GeneratedRegex("^\\$\\{\\{\\s*inputs\\.(?<input>[A-Za-z0-9_-]+)\\s*==\\s*'(?<value>[^']*)'\\s*\\}\\}$")]
    private static partial Regex InputConditionRegex();

    [GeneratedRegex("^\\$\\{\\{\\s*github\\.event\\.(?<path>[A-Za-z0-9_.-]+)\\s*==\\s*'(?<value>[^']*)'\\s*\\}\\}$")]
    private static partial Regex EventPayloadConditionRegex();
}
