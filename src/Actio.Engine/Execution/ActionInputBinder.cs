using Actio.Core.Actions;
using Actio.Core.Expressions;

namespace Actio.Engine.Execution;

internal static class ActionInputBinder
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

    public static ActionInputInterpolationResult InterpolateInputExpressions(
        string command,
        IReadOnlyDictionary<string, string> inputs,
        string workspaceRoot)
    {
        var interpolation = ExpressionTemplate.Interpolate(
            command,
            new ExpressionEvaluationContext(
                reference => ResolveInputReference(reference, inputs),
                workspaceRoot: workspaceRoot));

        return interpolation.Success
            ? ActionInputInterpolationResult.Resolved(interpolation.Value)
            : ActionInputInterpolationResult.Failed(interpolation.Errors);
    }

    private static string ToEnvironmentName(string inputName)
    {
        var segment = inputName
            .Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray();

        return $"INPUT_{new string(segment)}";
    }

    private static ExpressionReferenceResolution ResolveInputReference(
        ExpressionReference reference,
        IReadOnlyDictionary<string, string> inputs)
    {
        if (string.Equals(reference.Root, "inputs", StringComparison.Ordinal) && reference.Path.Count == 1)
        {
            return ExpressionReferenceResolution.Resolved(
                inputs.TryGetValue(reference.Path[0], out var value)
                    ? ExpressionValue.FromString(value)
                    : ExpressionValue.FromString(string.Empty));
        }

        return ExpressionReferenceResolution.Failed($"Unsupported expression reference '{reference}'.");
    }
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

internal sealed record ActionInputInterpolationResult(
    bool Success,
    string Value,
    IReadOnlyList<string> Errors)
{
    public static ActionInputInterpolationResult Resolved(string value)
        => new(true, value, []);

    public static ActionInputInterpolationResult Failed(IReadOnlyList<string> errors)
        => new(false, string.Empty, errors);
}
