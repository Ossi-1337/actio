using System.Text.RegularExpressions;

namespace Actio.Engine.Execution;

internal sealed partial class ConditionEvaluator
{
    public ConditionEvaluationResult Evaluate(
        string? expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        var match = SupportedConditionRegex().Match(expression);
        if (!match.Success)
        {
            return ConditionEvaluationResult.Failed("Unsupported if expression.");
        }

        var jobName = match.Groups["job"].Value;
        var outputName = match.Groups["output"].Value;
        var expectedValue = match.Groups["value"].Value;

        if (!jobOutputs.TryGetValue(jobName, out var outputs))
        {
            return ConditionEvaluationResult.Skip();
        }

        outputs.TryGetValue(outputName, out var actualValue);
        return string.Equals(actualValue, expectedValue, StringComparison.Ordinal)
            ? ConditionEvaluationResult.Run()
            : ConditionEvaluationResult.Skip();
    }

    [GeneratedRegex("^\\$\\{\\{\\s*needs\\.(?<job>[A-Za-z0-9_-]+)\\.outputs\\.(?<output>[A-Za-z0-9_-]+)\\s*==\\s*'(?<value>[^']*)'\\s*\\}\\}$")]
    private static partial Regex SupportedConditionRegex();
}
