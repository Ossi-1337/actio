using Actio.Core.Expressions;
using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal sealed class ConditionEvaluator
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

        return EvaluateExpression(expression, jobOutputs, inputs, eventPayload, []);
    }

    public ConditionEvaluationResult EvaluateStep(
        string? expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        IReadOnlyList<string> previousStepStatuses)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        return EvaluateExpression(expression, jobOutputs, inputs, eventPayload, previousStepStatuses);
    }

    private static ConditionEvaluationResult EvaluateExpression(
        string expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        IReadOnlyList<string> previousStepStatuses)
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(expression);
        if (!parseResult.Success)
        {
            return ConditionEvaluationResult.Failed($"Unsupported if expression: {string.Join(" ", parseResult.Errors)}");
        }

        var evaluation = ExpressionEvaluator.Evaluate(
            parseResult.Expression!,
            CreateContext(jobOutputs, inputs, eventPayload, previousStepStatuses));
        if (!evaluation.Success)
        {
            return ConditionEvaluationResult.Failed(string.Join(" ", evaluation.Errors));
        }

        return evaluation.Value.AsBoolean()
            ? ConditionEvaluationResult.Run()
            : ConditionEvaluationResult.Skip();
    }

    private static ExpressionEvaluationContext CreateContext(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        IReadOnlyList<string> previousStepStatuses)
    {
        return new ExpressionEvaluationContext(
            reference => ResolveReference(reference, jobOutputs, inputs, eventPayload),
            function => EvaluateStatusFunctionExpression(function, previousStepStatuses));
    }

    private static ExpressionReferenceResolution ResolveReference(
        ExpressionReference reference,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload)
    {
        if (string.Equals(reference.Root, "inputs", StringComparison.Ordinal) && reference.Path.Count == 1)
        {
            return ExpressionReferenceResolution.Resolved(
                inputs.TryGetValue(reference.Path[0], out var input)
                    ? ExpressionValue.FromString(input)
                    : ExpressionValue.Null);
        }

        if (string.Equals(reference.Root, "github", StringComparison.Ordinal) &&
            reference.Path.Count >= 2 &&
            string.Equals(reference.Path[0], "event", StringComparison.Ordinal))
        {
            var eventPath = string.Join(".", reference.Path.Skip(1));
            return ExpressionReferenceResolution.Resolved(
                eventPayload.GetValue(eventPath) is { } value
                    ? ExpressionValue.FromString(value)
                    : ExpressionValue.Null);
        }

        if (string.Equals(reference.Root, "needs", StringComparison.Ordinal) &&
            reference.Path.Count == 3 &&
            string.Equals(reference.Path[1], "outputs", StringComparison.Ordinal))
        {
            return ExpressionReferenceResolution.Resolved(
                jobOutputs.TryGetValue(reference.Path[0], out var outputs) &&
                outputs.TryGetValue(reference.Path[2], out var output)
                    ? ExpressionValue.FromString(output)
                    : ExpressionValue.Null);
        }

        return ExpressionReferenceResolution.Failed($"Unsupported expression reference '{reference}'.");
    }

    private static ExpressionEvaluationResult EvaluateStatusFunctionExpression(
        string function,
        IReadOnlyList<string> previousStepStatuses)
    {
        var condition = EvaluateStatusFunction(function, previousStepStatuses);
        if (!condition.Success)
        {
            return ExpressionEvaluationResult.Failed([condition.Error ?? "Unsupported status function."]);
        }

        return ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(condition.ShouldRun));
    }

    private static ConditionEvaluationResult EvaluateStatusFunction(
        string function,
        IReadOnlyList<string> previousStepStatuses)
    {
        return function switch
        {
            "always" => ConditionEvaluationResult.Run(),
            "success" => previousStepStatuses.Any(IsFailureStatus)
                ? ConditionEvaluationResult.Skip()
                : ConditionEvaluationResult.Run(),
            "failure" => previousStepStatuses.Any(IsFailureStatus)
                ? ConditionEvaluationResult.Run()
                : ConditionEvaluationResult.Skip(),
            "cancelled" => ConditionEvaluationResult.Skip(),
            _ => ConditionEvaluationResult.Failed("Unsupported status function.")
        };
    }

    private static bool IsFailureStatus(string status)
    {
        return string.Equals(status, "Failed", StringComparison.Ordinal) ||
            string.Equals(status, "TimedOut", StringComparison.Ordinal);
    }
}
