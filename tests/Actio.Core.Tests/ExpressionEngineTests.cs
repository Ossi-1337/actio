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
                (function, _) => ExpressionEvaluationResult.Resolved(ExpressionValue.FromBoolean(function.Name != "cancelled"))));

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
    public void Evaluate_SupportsStringAndFormatFunctions()
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(
            "${{ contains('Hello Actio', 'actio') && startsWith('Actio', 'ac') && endsWith('Actio', 'IO') && format('{0}-{1}', 'build', 42) == 'build-42' }}");

        Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));

        var evaluation = ExpressionEvaluator.Evaluate(parseResult.Expression!, new ExpressionEvaluationContext());

        Assert.True(evaluation.Success, string.Join(Environment.NewLine, evaluation.Errors));
        Assert.True(evaluation.Value.AsBoolean());
    }

    [Fact]
    public void Evaluate_SupportsJsonFunctionsAndJoin()
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(
            """${{ contains(fromJSON('["push","pull_request"]'), 'push') && join(fromJSON('["src","tests"]'), '/') == 'src/tests' && toJSON(fromJSON('{"ok":true}')) == '{"ok":true}' }}""");

        Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));

        var evaluation = ExpressionEvaluator.Evaluate(parseResult.Expression!, new ExpressionEvaluationContext());

        Assert.True(evaluation.Success, string.Join(Environment.NewLine, evaluation.Errors));
        Assert.True(evaluation.Value.AsBoolean());
    }

    [Fact]
    public async Task Evaluate_SupportsHashFilesWithWorkspaceRelativeGlobs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"actio-expression-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "src", "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "app.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "nested", "app.cs"), "code");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "skip.log"), "skip");

        try
        {
            var parseResult = ExpressionParser.ParseTemplateExpression("${{ hashFiles('src/app.txt') != '' && hashFiles('**/app.txt') != '' && hashFiles('src/*', '!src/*.log') == hashFiles('src/app.txt') && hashFiles('src/+.txt') == hashFiles('src/app.txt') && hashFiles('src\\app.txt') == hashFiles('src/app.txt') && hashFiles('**/*.cs') != '' && hashFiles('**/*.missing') == '' }}");

            Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));

            var evaluation = ExpressionEvaluator.Evaluate(
                parseResult.Expression!,
                new ExpressionEvaluationContext(workspaceRoot: root));

            Assert.True(evaluation.Success, string.Join(Environment.NewLine, evaluation.Errors));
            Assert.True(evaluation.Value.AsBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Evaluate_RejectsUnsafeHashFilesPatterns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"actio-expression-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var parseResult = ExpressionParser.ParseTemplateExpression("${{ hashFiles('../outside.txt') }}");

            Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));

            var evaluation = ExpressionEvaluator.Evaluate(
                parseResult.Expression!,
                new ExpressionEvaluationContext(workspaceRoot: root));

            Assert.False(evaluation.Success);
            Assert.Contains(evaluation.Errors, error => error.Contains("stay inside the workspace", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Interpolate_SupportsFunctionsAcrossMultipleTemplateExpressions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"actio-expression-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "app.txt"), "hello");

        try
        {
            var result = ExpressionTemplate.Interpolate(
                "echo \"${{ format('{0}{1}', inputs.name, inputs.punctuation) }} ${{ hashFiles('**/app.txt') != '' }}\"",
                new ExpressionEvaluationContext(
                    ResolveInputReference,
                    workspaceRoot: root));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal("echo \"Actio! true\"", result.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_ReturnsFunctionArgumentErrors()
    {
        var parseResult = ExpressionParser.ParseTemplateExpression("${{ contains('abc') }}");

        Assert.True(parseResult.Success, string.Join(Environment.NewLine, parseResult.Errors));

        var evaluation = ExpressionEvaluator.Evaluate(parseResult.Expression!, new ExpressionEvaluationContext());

        Assert.False(evaluation.Success);
        Assert.Contains(evaluation.Errors, error => error.Contains("contains() expects 2 argument", StringComparison.OrdinalIgnoreCase));
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
