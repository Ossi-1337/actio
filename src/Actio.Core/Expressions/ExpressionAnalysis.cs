namespace Actio.Core.Expressions;

public static class ExpressionAnalysis
{
    public static IReadOnlyList<ExpressionReference> CollectReferences(ExpressionNode expression)
    {
        var references = new List<ExpressionReference>();
        CollectReferences(expression, references);
        return references;
    }

    public static IReadOnlyList<ExpressionFunctionCall> CollectFunctionCalls(ExpressionNode expression)
    {
        var functions = new List<ExpressionFunctionCall>();
        CollectFunctionCalls(expression, functions);
        return functions;
    }

    private static void CollectReferences(ExpressionNode expression, List<ExpressionReference> references)
    {
        switch (expression)
        {
            case ReferenceExpressionNode reference:
                references.Add(reference.Reference);
                break;
            case UnaryExpressionNode unary:
                CollectReferences(unary.Operand, references);
                break;
            case BinaryExpressionNode binary:
                CollectReferences(binary.Left, references);
                CollectReferences(binary.Right, references);
                break;
        }
    }

    private static void CollectFunctionCalls(ExpressionNode expression, List<ExpressionFunctionCall> functions)
    {
        switch (expression)
        {
            case FunctionCallExpressionNode function:
                functions.Add(new ExpressionFunctionCall(function.Name));
                break;
            case UnaryExpressionNode unary:
                CollectFunctionCalls(unary.Operand, functions);
                break;
            case BinaryExpressionNode binary:
                CollectFunctionCalls(binary.Left, functions);
                CollectFunctionCalls(binary.Right, functions);
                break;
        }
    }
}

public static class ExpressionBuiltIns
{
    public static bool IsStatusFunction(string name)
    {
        return string.Equals(name, "success", StringComparison.Ordinal) ||
            string.Equals(name, "failure", StringComparison.Ordinal) ||
            string.Equals(name, "cancelled", StringComparison.Ordinal) ||
            string.Equals(name, "always", StringComparison.Ordinal);
    }
}
