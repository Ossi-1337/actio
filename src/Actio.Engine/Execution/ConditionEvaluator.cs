using Actio.Core.Expressions;
using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal sealed class ConditionEvaluator
{
    public ConditionEvaluationResult EvaluateJob(
        string? expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyList<string> neededJobs,
        string projectRoot)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        return EvaluateExpression(
            expression,
            jobOutputs,
            inputs,
            eventPayload,
            projectRoot,
            (function, arguments) => EvaluateJobStatusFunctionExpression(function, arguments, jobStatuses, neededJobs));
    }

    public ConditionEvaluationResult EvaluateStep(
        string? expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        IReadOnlyList<string> previousStepStatuses,
        string projectRoot)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        return EvaluateExpression(
            expression,
            jobOutputs,
            inputs,
            eventPayload,
            projectRoot,
            (function, arguments) => EvaluateStepStatusFunctionExpression(function, arguments, previousStepStatuses));
    }

    private static ConditionEvaluationResult EvaluateExpression(
        string expression,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        string projectRoot,
        Func<ExpressionFunctionCall, IReadOnlyList<ExpressionValue>, ExpressionEvaluationResult> evaluateFunction)
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(expression);
        if (!parseResult.Success)
        {
            return ConditionEvaluationResult.Failed($"Unsupported if expression: {string.Join(" ", parseResult.Errors)}");
        }

        var evaluation = ExpressionEvaluator.Evaluate(
            parseResult.Expression!,
            CreateContext(jobOutputs, inputs, eventPayload, projectRoot, evaluateFunction));
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
        string projectRoot,
        Func<ExpressionFunctionCall, IReadOnlyList<ExpressionValue>, ExpressionEvaluationResult> evaluateFunction)
    {
        return new ExpressionEvaluationContext(
            reference => ResolveReference(reference, jobOutputs, inputs, eventPayload),
            evaluateFunction,
            projectRoot);
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

    private static ExpressionEvaluationResult EvaluateStepStatusFunctionExpression(
        ExpressionFunctionCall function,
        IReadOnlyList<ExpressionValue> arguments,
        IReadOnlyList<string> previousStepStatuses)
    {
        var condition = EvaluateStatusFunction(function, arguments, previousStepStatuses, StatusFunctionScope.Step);
        if (!condition.Success)
        {
            return ExpressionEvaluationResult.Failed([condition.Error ?? "Unsupported status function."]);
        }

        return ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(condition.ShouldRun));
    }

    private static ExpressionEvaluationResult EvaluateJobStatusFunctionExpression(
        ExpressionFunctionCall function,
        IReadOnlyList<ExpressionValue> arguments,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyList<string> neededJobs)
    {
        var dependencyStatuses = neededJobs
            .Select(neededJob => jobStatuses.TryGetValue(neededJob, out var status) ? status : "Skipped")
            .ToArray();
        var condition = EvaluateStatusFunction(function, arguments, dependencyStatuses, StatusFunctionScope.Job);
        if (!condition.Success)
        {
            return ExpressionEvaluationResult.Failed([condition.Error ?? "Unsupported status function."]);
        }

        return ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(condition.ShouldRun));
    }

    private static ConditionEvaluationResult EvaluateStatusFunction(
        ExpressionFunctionCall function,
        IReadOnlyList<ExpressionValue> arguments,
        IReadOnlyList<string> statuses,
        StatusFunctionScope scope)
    {
        if (arguments.Count != 0)
        {
            return ConditionEvaluationResult.Failed($"{function.Name}() does not accept arguments.");
        }

        if (string.Equals(function.Name, "always", StringComparison.OrdinalIgnoreCase))
        {
            return ConditionEvaluationResult.Run();
        }

        if (string.Equals(function.Name, "success", StringComparison.OrdinalIgnoreCase))
        {
            return IsSuccessStatusFunction(statuses, scope)
                ? ConditionEvaluationResult.Run()
                : ConditionEvaluationResult.Skip();
        }

        if (string.Equals(function.Name, "failure", StringComparison.OrdinalIgnoreCase))
        {
            return statuses.Any(IsFailureStatus)
                ? ConditionEvaluationResult.Run()
                : ConditionEvaluationResult.Skip();
        }

        if (string.Equals(function.Name, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return ConditionEvaluationResult.Skip();
        }

        return ConditionEvaluationResult.Failed("Unsupported status function.");
    }

    private static bool IsSuccessStatusFunction(
        IReadOnlyList<string> statuses,
        StatusFunctionScope scope)
    {
        return scope == StatusFunctionScope.Job
            ? statuses.All(status => string.Equals(status, "Success", StringComparison.Ordinal))
            : !statuses.Any(IsFailureStatus);
    }

    private static bool IsFailureStatus(string status)
    {
        return string.Equals(status, "Failed", StringComparison.Ordinal) ||
            string.Equals(status, "TimedOut", StringComparison.Ordinal);
    }

    private enum StatusFunctionScope
    {
        Job,
        Step
    }
}
