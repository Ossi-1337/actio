using Actio.Core.Expressions;

namespace Actio.Core.Tests;

public sealed class ExpressionEngineTests
{
    [Fact]
    public void Evaluate_SupportsLiteralsComparisonsAndBooleanOperators()
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(
            "${{ inputs.environment == 'staging' && inputs.attempt >= 2 && !cancelled() }}");

        Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));

        var evaluation = ExpressionEvaluator.Evaluate(
            parseResult.Expression!,
            new ExpressionEvaluationContext(
                ResolveInputReference,
                function => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(function != "cancelled"))));

        Assert.True(evaluation.Success, string.Join(Environment.NewLine, evaluation.Errors));
        Assert.True(evaluation.Value.AsBoolean());
    }

    [Fact]
    public void Interpolate_ReplacesTemplateExpressionsInStrings()
    {
        var result = ExpressionTemplate.Interpolate(
            "Hello ${{ inputs.name }}${{ inputs.punctuation }}",
            new ExpressionEvaluationContext(ResolveInputReference));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("Hello Actio!", result.Value);
    }

    [Fact]
    public void ParseTemplateExpression_ReturnsActionableErrorsForInvalidSyntax()
    {
        var result = ExpressionParser.ParseTemplateExpression("${{ inputs.name == }}");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analysis_CollectsReferencesAndFunctionCalls()
    {
        var parseResult = ExpressionParser.ParseTemplateExpression("${{ needs.prepare.outputs.changed == 'true' || failure() }}");

        Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));
        Assert.Contains(
            ExpressionAnalysis.CollectReferences(parseResult.Expression!),
            reference => reference.ToString() == "needs.prepare.outputs.changed");
        Assert.Contains(
            ExpressionAnalysis.CollectFunctionCalls(parseResult.Expression!),
            function => function.Name == "failure");
    }

    private static ExpressionReferenceResolution ResolveInputReference(ExpressionReference reference)
    {
        if (!string.Equals(reference.Root, "inputs", StringComparison.Ordinal) || reference.Path.Count != 1)
        {
            return ExpressionReferenceResolution.Failed($"Unsupported expression reference '{reference}'.");
        }

        var value = reference.Path[0] switch
        {
            "environment" => "staging",
            "attempt" => "2",
            "name" => "Actio",
            "punctuation" => "!",
            _ => string.Empty
        };

        return ExpressionReferenceResolution.Resolved(ExpressionValue.FromString(value));
    }
}
