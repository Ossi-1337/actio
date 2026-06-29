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
            case FunctionCallExpressionNode function:
                foreach (var argument in function.Arguments)
                {
                    CollectReferences(argument, references);
                }

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
                foreach (var argument in function.Arguments)
                {
                    CollectFunctionCalls(argument, functions);
                }

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
        return string.Equals(name, "success", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "failure", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "always", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDeterministicFunction(string name)
    {
        return string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "startsWith", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "endsWith", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "format", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "join", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "toJSON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "fromJSON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "hashFiles", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedFunction(string name)
    {
        return IsStatusFunction(name) || IsDeterministicFunction(name);
    }
}
