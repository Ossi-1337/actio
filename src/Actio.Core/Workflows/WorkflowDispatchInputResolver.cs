using System.Globalization;

namespace Actio.Core.Workflows;

public sealed record WorkflowDispatchInputResolutionResult(
    bool Success,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyList<string> Errors)
{
    public static WorkflowDispatchInputResolutionResult Resolved(IReadOnlyDictionary<string, string> inputs)
        => new(true, inputs, []);

    public static WorkflowDispatchInputResolutionResult Failed(IReadOnlyList<string> errors)
        => new(false, new Dictionary<string, string>(), errors);
}

public static class WorkflowDispatchInputResolver
{
    public static WorkflowDispatchInputResolutionResult Resolve(
        WorkflowDocument workflow,
        IReadOnlyDictionary<string, string> providedInputs)
    {
        var dispatch = workflow.Triggers
            .FirstOrDefault(trigger => string.Equals(trigger.EventName, "workflow_dispatch", StringComparison.Ordinal))?
            .Dispatch;

        if (dispatch is null)
        {
            return providedInputs.Count == 0
                ? WorkflowDispatchInputResolutionResult.Resolved(new Dictionary<string, string>())
                : WorkflowDispatchInputResolutionResult.Failed(["Workflow does not define workflow_dispatch, so manual inputs cannot be used."]);
        }

        return Resolve(dispatch.Inputs, providedInputs);
    }

    private static WorkflowDispatchInputResolutionResult Resolve(
        IReadOnlyDictionary<string, WorkflowDispatchInput> definitions,
        IReadOnlyDictionary<string, string> providedInputs)
    {
        var errors = new List<string>();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in providedInputs)
        {
            if (!definitions.ContainsKey(input.Key))
            {
                errors.Add($"workflow_dispatch input '{input.Key}' is not declared.");
            }
        }

        foreach (var definition in definitions.Values)
        {
            var hasProvidedValue = providedInputs.TryGetValue(definition.Name, out var value);
            if (!hasProvidedValue)
            {
                value = definition.Default;
            }

            if (value is null)
            {
                if (definition.Required)
                {
                    errors.Add($"workflow_dispatch input '{definition.Name}' is required.");
                }

                continue;
            }

            if (definition.Required && value.Length == 0)
            {
                errors.Add($"workflow_dispatch input '{definition.Name}' is required.");
                continue;
            }

            ValidateInputValue(errors, definition, value);
            resolved[definition.Name] = value;
        }

        return errors.Count == 0
            ? WorkflowDispatchInputResolutionResult.Resolved(resolved)
            : WorkflowDispatchInputResolutionResult.Failed(errors);
    }

    private static void ValidateInputValue(
        List<string> errors,
        WorkflowDispatchInput definition,
        string value)
    {
        if (string.Equals(definition.Type, "choice", StringComparison.Ordinal) &&
            !definition.Options.Contains(value, StringComparer.Ordinal))
        {
            errors.Add($"workflow_dispatch input '{definition.Name}' must be one of: {string.Join(", ", definition.Options)}.");
            return;
        }

        if (string.Equals(definition.Type, "boolean", StringComparison.Ordinal) &&
            !bool.TryParse(value, out _))
        {
            errors.Add($"workflow_dispatch input '{definition.Name}' must be true or false.");
            return;
        }

        if (string.Equals(definition.Type, "number", StringComparison.Ordinal) &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            errors.Add($"workflow_dispatch input '{definition.Name}' must be a number.");
        }
    }
}
