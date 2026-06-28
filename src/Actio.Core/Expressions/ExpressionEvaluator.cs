namespace Actio.Core.Expressions;

public static class ExpressionEvaluator
{
    public static ExpressionEvaluationResult Evaluate(
        ExpressionNode expression,
        ExpressionEvaluationContext context)
    {
        return expression switch
        {
            LiteralExpressionNode literal => ExpressionEvaluationResult.Resolved(literal.Value),
            ReferenceExpressionNode reference => ResolveReference(reference.Reference, context),
            FunctionCallExpressionNode function => context.EvaluateFunction(function.Name),
            UnaryExpressionNode unary => EvaluateUnary(unary, context),
            BinaryExpressionNode binary => EvaluateBinary(binary, context),
            _ => ExpressionEvaluationResult.Failed(["Unsupported expression node."])
        };
    }

    private static ExpressionEvaluationResult ResolveReference(
        ExpressionReference reference,
        ExpressionEvaluationContext context)
    {
        var resolution = context.ResolveReference(reference);
        return resolution.Success
            ? ExpressionEvaluationResult.Resolved(resolution.Value)
            : ExpressionEvaluationResult.Failed([resolution.Error ?? $"Unsupported expression reference '{reference}'."]);
    }

    private static ExpressionEvaluationResult EvaluateUnary(
        UnaryExpressionNode unary,
        ExpressionEvaluationContext context)
    {
        var operand = Evaluate(unary.Operand, context);
        if (!operand.Success)
        {
            return operand;
        }

        return unary.Operator switch
        {
            ExpressionUnaryOperator.Not => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(!operand.Value.AsBoolean())),
            _ => ExpressionEvaluationResult.Failed(["Unsupported unary expression operator."])
        };
    }

    private static ExpressionEvaluationResult EvaluateBinary(
        BinaryExpressionNode binary,
        ExpressionEvaluationContext context)
    {
        if (binary.Operator == ExpressionBinaryOperator.And)
        {
            var left = Evaluate(binary.Left, context);
            if (!left.Success || !left.Value.AsBoolean())
            {
                return left.Success
                    ? ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(false))
                    : left;
            }

            var right = Evaluate(binary.Right, context);
            return right.Success
                ? ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(right.Value.AsBoolean()))
                : right;
        }

        if (binary.Operator == ExpressionBinaryOperator.Or)
        {
            var left = Evaluate(binary.Left, context);
            if (!left.Success || left.Value.AsBoolean())
            {
                return left.Success
                    ? ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(true))
                    : left;
            }

            var right = Evaluate(binary.Right, context);
            return right.Success
                ? ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(right.Value.AsBoolean()))
                : right;
        }

        var leftValue = Evaluate(binary.Left, context);
        if (!leftValue.Success)
        {
            return leftValue;
        }

        var rightValue = Evaluate(binary.Right, context);
        if (!rightValue.Success)
        {
            return rightValue;
        }

        return binary.Operator switch
        {
            ExpressionBinaryOperator.Equal => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(AreEqual(leftValue.Value, rightValue.Value))),
            ExpressionBinaryOperator.NotEqual => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(!AreEqual(leftValue.Value, rightValue.Value))),
            ExpressionBinaryOperator.LessThan => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(Compare(leftValue.Value, rightValue.Value) < 0)),
            ExpressionBinaryOperator.LessThanOrEqual => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(Compare(leftValue.Value, rightValue.Value) <= 0)),
            ExpressionBinaryOperator.GreaterThan => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(Compare(leftValue.Value, rightValue.Value) > 0)),
            ExpressionBinaryOperator.GreaterThanOrEqual => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(Compare(leftValue.Value, rightValue.Value) >= 0)),
            _ => ExpressionEvaluationResult.Failed(["Unsupported binary expression operator."])
        };
    }

    private static bool AreEqual(ExpressionValue left, ExpressionValue right)
    {
        if (left.Kind == ExpressionValueKind.Null || right.Kind == ExpressionValueKind.Null)
        {
            return left.Kind == right.Kind;
        }

        if (left.TryGetNumber(out var leftNumber) && right.TryGetNumber(out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(left.AsString(), right.AsString(), StringComparison.Ordinal);
    }

    private static int Compare(ExpressionValue left, ExpressionValue right)
    {
        if (left.TryGetNumber(out var leftNumber) && right.TryGetNumber(out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.CompareOrdinal(left.AsString(), right.AsString());
    }
}
