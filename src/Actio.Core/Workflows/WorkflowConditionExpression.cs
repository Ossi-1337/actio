using System.Text.RegularExpressions;

namespace Actio.Core.Workflows;

public sealed partial record WorkflowConditionExpression(
    string ReferencedJob,
    string OutputName,
    string ExpectedValue)
{
    public static bool TryParse(string expression, out WorkflowConditionExpression? condition)
    {
        var match = SupportedConditionRegex().Match(expression);
        if (!match.Success)
        {
            condition = null;
            return false;
        }

        condition = new WorkflowConditionExpression(
            match.Groups["job"].Value,
            match.Groups["output"].Value,
            match.Groups["value"].Value);
        return true;
    }

    [GeneratedRegex("^\\$\\{\\{\\s*needs\\.(?<job>[A-Za-z0-9_-]+)\\.outputs\\.(?<output>[A-Za-z0-9_-]+)\\s*==\\s*'(?<value>[^']*)'\\s*\\}\\}$")]
    private static partial Regex SupportedConditionRegex();
}
