using System.Text;

namespace Actio.Core.Expressions;

public static class ExpressionTemplate
{
    public static ExpressionInterpolationResult Interpolate(
        string value,
        ExpressionEvaluationContext context)
    {
        var output = new StringBuilder();
        var index = 0;
        var errors = new List<string>();

        while (index < value.Length)
        {
            var expressionStart = value.IndexOf("${{", index, StringComparison.Ordinal);
            if (expressionStart < 0)
            {
                output.Append(value[index..]);
                break;
            }

            output.Append(value[index..expressionStart]);
            var expressionEnd = value.IndexOf("}}", expressionStart + 3, StringComparison.Ordinal);
            if (expressionEnd < 0)
            {
                errors.Add("Unclosed expression interpolation.");
                break;
            }

            var expressionText = value[expressionStart..(expressionEnd + 2)];
            var parseResult = ExpressionParser.ParseTemplateExpression(expressionText);
            if (!parseResult.Success)
            {
                errors.AddRange(parseResult.Errors);
                index = expressionEnd + 2;
                continue;
            }

            var evaluation = ExpressionEvaluator.Evaluate(parseResult.Expression!, context);
            if (!evaluation.Success)
            {
                errors.AddRange(evaluation.Errors);
                index = expressionEnd + 2;
                continue;
            }

            output.Append(evaluation.Value.AsString());
            index = expressionEnd + 2;
        }

        return errors.Count == 0
            ? ExpressionInterpolationResult.Resolved(output.ToString())
            : ExpressionInterpolationResult.Failed(errors);
    }
}
