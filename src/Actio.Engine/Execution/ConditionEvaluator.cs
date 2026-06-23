using Actio.Core.Workflows;

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

        if (!WorkflowConditionExpression.TryParse(expression, out var condition))
        {
            return ConditionEvaluationResult.Failed("Unsupported if expression.");
        }

        if (!jobOutputs.TryGetValue(condition!.ReferencedJob, out var outputs))
        {
            return ConditionEvaluationResult.Skip();
        }

        outputs.TryGetValue(condition.OutputName, out var actualValue);
        return string.Equals(actualValue, condition.ExpectedValue, StringComparison.Ordinal)
            ? ConditionEvaluationResult.Run()
            : ConditionEvaluationResult.Skip();
    }
}
