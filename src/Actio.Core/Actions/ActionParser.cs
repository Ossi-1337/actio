using YamlDotNet.RepresentationModel;

namespace Actio.Core.Actions;

public sealed class ActionParser
{
    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "author",
        "branding",
        "description",
        "inputs",
        "name",
        "outputs",
        "runs"
    };

    private static readonly HashSet<string> RunsKeys = new(StringComparer.Ordinal)
    {
        "using",
        "steps"
    };

    private static readonly HashSet<string> InputKeys = new(StringComparer.Ordinal)
    {
        "default",
        "deprecationMessage",
        "description",
        "required"
    };

    private static readonly HashSet<string> StepKeys = new(StringComparer.Ordinal)
    {
        "name",
        "run",
        "shell"
    };

    public ActionParseResult ParseFile(string actionPath)
    {
        try
        {
            using var reader = File.OpenText(actionPath);
            return Parse(reader);
        }
        catch (IOException ex)
        {
            return ActionParseResult.Failed([$"Could not read action file: {ex.Message}"]);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ActionParseResult.Failed([$"Could not read action file: {ex.Message}"]);
        }
        catch (NotSupportedException ex)
        {
            return ActionParseResult.Failed([$"Could not read action file: {ex.Message}"]);
        }
        catch (ArgumentException ex)
        {
            return ActionParseResult.Failed([$"Could not read action file: {ex.Message}"]);
        }
    }

    public ActionParseResult Parse(TextReader reader)
    {
        var errors = new List<string>();
        var yaml = new YamlStream();

        try
        {
            yaml.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return ActionParseResult.Failed([$"Action YAML could not be parsed: {ex.Message}"]);
        }

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return ActionParseResult.Failed(["Action file must contain a YAML mapping at the root."]);
        }

        AddUnknownKeyErrors(errors, root, TopLevelKeys, "action");

        var name = ReadRequiredScalar(errors, root, "name", "action.name");
        var inputs = ReadInputs(errors, root);
        var steps = ReadRuns(errors, root);

        if (errors.Count > 0)
        {
            return ActionParseResult.Failed(errors);
        }

        return ActionParseResult.Parsed(new ActionDocument(name!, steps, inputs));
    }

    private static IReadOnlyDictionary<string, ActionInput> ReadInputs(List<string> errors, YamlMappingNode root)
    {
        if (!TryGet(root, "inputs", out var inputsNode))
        {
            return new Dictionary<string, ActionInput>();
        }

        if (inputsNode is not YamlMappingNode inputsMap)
        {
            errors.Add("action.inputs must be a mapping.");
            return new Dictionary<string, ActionInput>();
        }

        var inputs = new Dictionary<string, ActionInput>(StringComparer.Ordinal);

        foreach (var (keyNode, valueNode) in inputsMap.Children)
        {
            var inputName = ReadMapKey(errors, keyNode, "action.inputs");
            if (inputName is null)
            {
                continue;
            }

            var inputPath = $"action.inputs.{inputName}";
            if (valueNode is not YamlMappingNode inputMap)
            {
                errors.Add($"{inputPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, inputMap, InputKeys, inputPath);
            var description = ReadOptionalScalar(errors, inputMap, "description", $"{inputPath}.description");
            var required = ReadOptionalBoolean(errors, inputMap, "required", $"{inputPath}.required") ?? false;
            var defaultValue = ReadOptionalScalar(errors, inputMap, "default", $"{inputPath}.default");

            inputs[inputName] = new ActionInput(inputName, description, required, defaultValue);
        }

        return inputs;
    }

    private static IReadOnlyList<ActionStep> ReadRuns(List<string> errors, YamlMappingNode root)
    {
        if (!TryGet(root, "runs", out var runsNode))
        {
            errors.Add("action.runs is required.");
            return [];
        }

        if (runsNode is not YamlMappingNode runsMap)
        {
            errors.Add("action.runs must be a mapping.");
            return [];
        }

        AddUnknownKeyErrors(errors, runsMap, RunsKeys, "action.runs");

        var usingValue = ReadRequiredScalar(errors, runsMap, "using", "action.runs.using");
        if (usingValue is not null && !string.Equals(usingValue, "composite", StringComparison.Ordinal))
        {
            errors.Add("action.runs.using supports only 'composite'.");
        }

        return ReadSteps(errors, runsMap);
    }

    private static IReadOnlyList<ActionStep> ReadSteps(List<string> errors, YamlMappingNode runsMap)
    {
        if (!TryGet(runsMap, "steps", out var stepsNode))
        {
            errors.Add("action.runs.steps is required.");
            return [];
        }

        if (stepsNode is not YamlSequenceNode sequence)
        {
            errors.Add("action.runs.steps must be a list.");
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            errors.Add("action.runs.steps must contain at least one step.");
            return [];
        }

        var steps = new List<ActionStep>();

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var itemPath = $"action.runs.steps[{index}]";

            if (sequence.Children[index] is not YamlMappingNode stepMap)
            {
                errors.Add($"{itemPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, stepMap, StepKeys, itemPath);

            var name = ReadRequiredScalar(errors, stepMap, "name", $"{itemPath}.name");
            var run = ReadRequiredScalar(errors, stepMap, "run", $"{itemPath}.run");

            if (name is not null && run is not null)
            {
                steps.Add(new ActionStep(name, run));
            }
        }

        return steps;
    }

    private static string? ReadRequiredScalar(List<string> errors, YamlMappingNode map, string key, string path)
    {
        if (!TryGet(map, key, out var node))
        {
            errors.Add($"{path} is required.");
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            errors.Add($"{path} must be a string.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            errors.Add($"{path} cannot be empty.");
            return null;
        }

        return scalar.Value;
    }

    private static string? ReadOptionalScalar(List<string> errors, YamlMappingNode map, string key, string path)
    {
        if (!TryGet(map, key, out var node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            errors.Add($"{path} must be a string.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            errors.Add($"{path} cannot be empty.");
            return null;
        }

        return scalar.Value;
    }

    private static bool? ReadOptionalBoolean(List<string> errors, YamlMappingNode map, string key, string path)
    {
        var value = ReadOptionalScalar(errors, map, key, path);
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        errors.Add($"{path} must be true or false.");
        return null;
    }

    private static string? ReadMapKey(List<string> errors, YamlNode keyNode, string path)
    {
        if (keyNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            errors.Add($"{path} contains an empty or non-string key.");
            return null;
        }

        return scalar.Value;
    }

    private static void AddUnknownKeyErrors(
        List<string> errors,
        YamlMappingNode map,
        IReadOnlySet<string> allowedKeys,
        string path)
    {
        foreach (var keyNode in map.Children.Keys)
        {
            if (keyNode is not YamlScalarNode scalar || scalar.Value is null)
            {
                continue;
            }

            if (!allowedKeys.Contains(scalar.Value))
            {
                errors.Add($"{path}.{scalar.Value} is not supported.");
            }
        }
    }

    private static bool TryGet(YamlMappingNode map, string key, out YamlNode node)
    {
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                node = valueNode;
                return true;
            }
        }

        node = null!;
        return false;
    }
}
