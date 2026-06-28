using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal sealed partial class ConditionEvaluator
{
    public ConditionEvaluationResult Evaluate(
        string? expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        if (!WorkflowConditionExpression.TryParse(expression, out var condition))
        {
            return ConditionEvaluationResult.Failed("Unsupported if expression.");
        }

        if (condition!.Kind == WorkflowConditionExpressionKind.Input)
        {
            inputs.TryGetValue(condition.Name, out var actualInputValue);
            return string.Equals(actualInputValue, condition.ExpectedValue, StringComparison.Ordinal)
                ? ConditionEvaluationResult.Run()
                : ConditionEvaluationResult.Skip();
        }

        if (condition.Kind == WorkflowConditionExpressionKind.EventPayload)
        {
            var actualPayloadValue = eventPayload.GetValue(condition.Name);
            return string.Equals(actualPayloadValue, condition.ExpectedValue, StringComparison.Ordinal)
                ? ConditionEvaluationResult.Run()
                : ConditionEvaluationResult.Skip();
        }

        if (!jobOutputs.TryGetValue(condition.ReferencedJob!, out var outputs))
        {
            return ConditionEvaluationResult.Skip();
        }

        outputs.TryGetValue(condition.Name, out var actualValue);
        return string.Equals(actualValue, condition.ExpectedValue, StringComparison.Ordinal)
            ? ConditionEvaluationResult.Run()
            : ConditionEvaluationResult.Skip();
    }
}
