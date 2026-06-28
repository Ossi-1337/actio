using System.Text.RegularExpressions;
using Actio.Core.Actions;

namespace Actio.Engine.Execution;

internal static partial class ActionInputBinder
{
    public static ActionInputBindingResult Bind(
        ActionDocument action,
        IReadOnlyDictionary<string, string> providedInputs)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var input in action.Inputs.Values)
        {
            if (input.Default is not null)
            {
                inputs[input.Name] = input.Default;
            }
        }

        foreach (var input in providedInputs)
        {
            inputs[input.Key] = input.Value;
        }

        foreach (var input in action.Inputs.Values)
        {
            if (input.Required && !inputs.ContainsKey(input.Name))
            {
                errors.Add($"action.inputs.{input.Name} is required but no with.{input.Name} value or default is defined.");
            }
        }

        return errors.Count > 0
            ? ActionInputBindingResult.Failed(errors)
            : ActionInputBindingResult.Resolved(inputs, CreateEnvironment(inputs));
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironment(
        IReadOnlyDictionary<string, string> inputs)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            environment[ToEnvironmentName(input.Key)] = input.Value;
        }

        return environment;
    }

    public static string InterpolateInputExpressions(
        string command,
        IReadOnlyDictionary<string, string> inputs)
    {
        return InputExpressionRegex().Replace(
            command,
            match => inputs.TryGetValue(match.Groups["name"].Value, out var value)
                ? value
                : string.Empty);
    }

    private static string ToEnvironmentName(string inputName)
    {
        var segment = inputName
            .Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray();

        return $"INPUT_{new string(segment)}";
    }

    [GeneratedRegex("\\$\\{\\{\\s*inputs\\.(?<name>[A-Za-z0-9_-]+)\\s*\\}\\}")]
    private static partial Regex InputExpressionRegex();
}

internal sealed record ActionInputBindingResult(
    bool Success,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Errors)
{
    public static ActionInputBindingResult Resolved(
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> environment)
    {
        return new ActionInputBindingResult(true, inputs, environment, []);
    }

    public static ActionInputBindingResult Failed(IReadOnlyList<string> errors)
    {
        return new ActionInputBindingResult(
            false,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            errors);
    }
}
