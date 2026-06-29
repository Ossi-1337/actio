using Actio.Core.Expressions;
using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal sealed class ConditionEvaluator
{
    public ConditionEvaluationResult EvaluateJob(
        string? expression,
        ExpressionContextData contextData,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyList<string> neededJobs)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        return EvaluateExpression(
            expression,
            contextData,
            (function, arguments) => EvaluateJobStatusFunctionExpression(function, arguments, jobStatuses, neededJobs));
    }

    public ConditionEvaluationResult EvaluateStep(
        string? expression,
        ExpressionContextData contextData,
        IReadOnlyList<string> previousStepStatuses)
    {
        if (expression is null)
        {
            return ConditionEvaluationResult.Run();
        }

        return EvaluateExpression(
            expression,
            contextData,
            (function, arguments) => EvaluateStepStatusFunctionExpression(function, arguments, previousStepStatuses));
    }

    private static ConditionEvaluationResult EvaluateExpression(
        string expression,
        ExpressionContextData contextData,
        Func<ExpressionFunctionCall, IReadOnlyList<ExpressionValue>, ExpressionEvaluationResult> evaluateFunction)
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(expression);
        if (!parseResult.Success)
        {
            return ConditionEvaluationResult.Failed($"Unsupported if expression: {string.Join(" ", parseResult.Errors)}");
        }

        var evaluation = ExpressionEvaluator.Evaluate(
            parseResult.Expression!,
            new ExpressionEvaluationContext(
                contextData.Resolve,
                evaluateFunction,
                contextData.WorkspaceRoot));
        if (!evaluation.Success)
        {
            return ConditionEvaluationResult.Failed(string.Join(" ", evaluation.Errors));
        }

        return evaluation.Value.AsBoolean()
            ? ConditionEvaluationResult.Run()
            : ConditionEvaluationResult.Skip();
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
