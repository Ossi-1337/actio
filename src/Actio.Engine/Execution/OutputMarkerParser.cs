using System.Text.RegularExpressions;

namespace Actio.Engine.Execution;

internal sealed partial class OutputMarkerParser
{
    public IReadOnlyDictionary<string, string> Parse(IReadOnlyList<string> outputLines)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in outputLines)
        {
            if (!TryParse(line, out var output))
            {
                continue;
            }

            outputs[output.Key] = output.Value;
        }

        return outputs;
    }

    public bool TryParse(string line, out KeyValuePair<string, string> output)
    {
        var match = OutputMarkerRegex().Match(line);
        if (!match.Success)
        {
            output = default;
            return false;
        }

        output = new KeyValuePair<string, string>(
            match.Groups["name"].Value,
            match.Groups["value"].Value);
        return true;
    }

    [GeneratedRegex("^actio\\.output\\s+(?<name>[A-Za-z0-9_-]+)=(?<value>.*)$")]
    private static partial Regex OutputMarkerRegex();
}
