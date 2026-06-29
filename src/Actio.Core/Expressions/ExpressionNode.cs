namespace Actio.Core.Expressions;

public abstract record ExpressionNode;

public sealed record LiteralExpressionNode(ExpressionValue Value) : ExpressionNode;

public sealed record ReferenceExpressionNode(ExpressionReference Reference) : ExpressionNode;

public sealed record FunctionCallExpressionNode(
    string Name,
    IReadOnlyList<ExpressionNode> Arguments) : ExpressionNode;

public sealed record UnaryExpressionNode(
    ExpressionUnaryOperator Operator,
    ExpressionNode Operand) : ExpressionNode;

public sealed record BinaryExpressionNode(
    ExpressionNode Left,
    ExpressionBinaryOperator Operator,
    ExpressionNode Right) : ExpressionNode;

public enum ExpressionUnaryOperator
{
    Not
}

public enum ExpressionBinaryOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    And,
    Or
}

public sealed record ExpressionReference(string Root, IReadOnlyList<string> Path)
{
    public override string ToString()
        => Path.Count == 0 ? Root : $"{Root}.{string.Join(".", Path)}";
}

public sealed record ExpressionFunctionCall(string Name);
