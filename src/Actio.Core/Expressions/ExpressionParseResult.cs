namespace Actio.Core.Expressions;

public sealed record ExpressionParseResult(
    bool Success,
    ExpressionNode? Expression,
    IReadOnlyList<string> Errors)
{
    public static ExpressionParseResult Resolved(ExpressionNode expression)
        => new(true, expression, []);

    public static ExpressionParseResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}

public sealed record ExpressionInterpolationResult(
    bool Success,
    string Value,
    IReadOnlyList<string> Errors)
{
    public static ExpressionInterpolationResult Resolved(string value)
        => new(true, value, []);

    public static ExpressionInterpolationResult Failed(IReadOnlyList<string> errors)
        => new(false, string.Empty, errors);
}
