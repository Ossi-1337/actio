namespace Actio.Core.Expressions;

public sealed class ExpressionEvaluationContext
{
    public ExpressionEvaluationContext(
        Func<ExpressionReference, ExpressionReferenceResolution>? resolveReference = null,
        Func<string, ExpressionEvaluationResult>? evaluateFunction = null)
    {
        ResolveReference = resolveReference ?? (reference => ExpressionReferenceResolution.Failed($"Unsupported expression reference '{reference}'."));
        EvaluateFunction = evaluateFunction ?? (name => ExpressionEvaluationResult.Failed([$"Unsupported expression function '{name}'."]));
    }

    public Func<ExpressionReference, ExpressionReferenceResolution> ResolveReference { get; }

    public Func<string, ExpressionEvaluationResult> EvaluateFunction { get; }
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
