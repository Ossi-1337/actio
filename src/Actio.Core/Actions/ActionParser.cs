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
        "main",
        "post",
        "post-if",
        "pre",
        "pre-if",
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
        var runs = ReadRuns(errors, root);

        if (errors.Count > 0)
        {
            return ActionParseResult.Failed(errors);
        }

        return ActionParseResult.Parsed(new ActionDocument(
            name!,
            runs.Steps,
            inputs,
            runs.Runtime,
            runs.Main,
            runs.Pre,
            runs.Post));
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

    private static ActionRuns ReadRuns(List<string> errors, YamlMappingNode root)
    {
        if (!TryGet(root, "runs", out var runsNode))
        {
            errors.Add("action.runs is required.");
            return ActionRuns.Composite([]);
        }

        if (runsNode is not YamlMappingNode runsMap)
        {
            errors.Add("action.runs must be a mapping.");
            return ActionRuns.Composite([]);
        }

        AddUnknownKeyErrors(errors, runsMap, RunsKeys, "action.runs");
        AddUnsupportedKeyError(errors, runsMap, "pre-if", "action.runs.pre-if");
        AddUnsupportedKeyError(errors, runsMap, "post-if", "action.runs.post-if");

        var usingValue = ReadRequiredScalar(errors, runsMap, "using", "action.runs.using");
        if (usingValue is null)
        {
            return ActionRuns.Composite([]);
        }

        if (string.Equals(usingValue, ActionRuntime.Composite, StringComparison.Ordinal))
        {
            return ActionRuns.Composite(ReadSteps(errors, runsMap));
        }

        if (string.Equals(usingValue, ActionRuntime.Node20, StringComparison.Ordinal))
        {
            if (TryGet(runsMap, "steps", out _))
            {
                errors.Add("action.runs.steps is supported only when action.runs.using is 'composite'.");
            }

            var main = ReadRequiredScalar(errors, runsMap, "main", "action.runs.main");
            var pre = ReadOptionalScalar(errors, runsMap, "pre", "action.runs.pre");
            var post = ReadOptionalScalar(errors, runsMap, "post", "action.runs.post");

            ValidateActionPath(errors, main, "action.runs.main");
            ValidateActionPath(errors, pre, "action.runs.pre");
            ValidateActionPath(errors, post, "action.runs.post");

            return ActionRuns.JavaScript(main, pre, post);
        }

        errors.Add("action.runs.using supports only 'composite' or 'node20'.");
        return ActionRuns.Composite([]);
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

    private static void ValidateActionPath(List<string> errors, string? value, string path)
    {
        if (value is null)
        {
            return;
        }

        if (IsRootedActionPath(value))
        {
            errors.Add($"{path} must be a relative path inside the action directory.");
            return;
        }

        if (value
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            errors.Add($"{path} must be a relative path inside the action directory.");
        }
    }

    private static bool IsRootedActionPath(string value)
    {
        return Path.IsPathRooted(value) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("\\", StringComparison.Ordinal) ||
            (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':');
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

    private static void AddUnsupportedKeyError(
        List<string> errors,
        YamlMappingNode map,
        string key,
        string path)
    {
        if (TryGet(map, key, out _))
        {
            errors.Add($"{path} is not supported.");
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

    private sealed record ActionRuns(
        string Runtime,
        IReadOnlyList<ActionStep> Steps,
        string? Main,
        string? Pre,
        string? Post)
    {
        public static ActionRuns Composite(IReadOnlyList<ActionStep> steps)
            => new(ActionRuntime.Composite, steps, null, null, null);

        public static ActionRuns JavaScript(string? main, string? pre, string? post)
            => new(ActionRuntime.Node20, [], main, pre, post);
    }
}
