namespace Actio.Core.Expressions;

public sealed class ExpressionEvaluationContext
{
    public ExpressionEvaluationContext(
        Func<ExpressionReference, ExpressionReferenceResolution>? resolveReference = null,
        Func<ExpressionFunctionCall, IReadOnlyList<ExpressionValue>, ExpressionEvaluationResult>? evaluateFunction = null,
        string? workspaceRoot = null)
    {
        ResolveReference = resolveReference ?? (reference => ExpressionReferenceResolution.Failed($"Unsupported expression reference '{reference}'."));
        EvaluateFunction = evaluateFunction ?? ((function, _) => ExpressionEvaluationResult.Failed([$"Unsupported expression function '{function.Name}'."]));
        WorkspaceRoot = workspaceRoot;
    }

    public Func<ExpressionReference, ExpressionReferenceResolution> ResolveReference { get; }

    public Func<ExpressionFunctionCall, IReadOnlyList<ExpressionValue>, ExpressionEvaluationResult> EvaluateFunction { get; }

    public string? WorkspaceRoot { get; }
}

public sealed record ExpressionReferenceResolution(
    bool Success,
    ExpressionValue Value,
    string? Error)
{
    public static ExpressionReferenceResolution Resolved(ExpressionValue value)
        => new(true, value, null);

    public static ExpressionReferenceResolution Failed(string error)
        => new(false, ExpressionValue.Null, error);
}

public sealed record ExpressionEvaluationResult(
    bool Success,
    ExpressionValue Value,
    IReadOnlyList<string> Errors)
{
    public static ExpressionEvaluationResult Resolved(ExpressionValue value)
        => new(true, value, []);

    public static ExpressionEvaluationResult Failed(IReadOnlyList<string> errors)
        => new(false, ExpressionValue.Null, errors);
}
