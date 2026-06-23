using System.Text.RegularExpressions;

namespace Actio.Engine.Execution;

internal sealed partial class OutputMarkerParser
{
    public IReadOnlyDictionary<string, string> Parse(IReadOnlyList<string> outputLines)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in outputLines)
        {
            var match = OutputMarkerRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            outputs[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return outputs;
    }

    [GeneratedRegex("^actio\\.output\\s+(?<name>[A-Za-z0-9_-]+)=(?<value>.*)$")]
    private static partial Regex OutputMarkerRegex();
}
