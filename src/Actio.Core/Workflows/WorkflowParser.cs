using Actio.Core.Actions;
using YamlDotNet.RepresentationModel;

namespace Actio.Core.Workflows;

public sealed partial class WorkflowParser
{
    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "name",
        "env",
        "jobs"
    };

    private static readonly HashSet<string> JobKeys = new(StringComparer.Ordinal)
    {
        "needs",
        "if",
        "runs-on",
        "outputs",
        "artifacts",
        "steps"
    };

    private static readonly HashSet<string> ArtifactKeys = new(StringComparer.Ordinal)
    {
        "name",
        "path"
    };

    private static readonly HashSet<string> StepKeys = new(StringComparer.Ordinal)
    {
        "name",
        "run",
        "uses"
    };

    public WorkflowParseResult ParseFile(string workflowPath)
    {
        try
        {
            using var reader = File.OpenText(workflowPath);
            return Parse(reader);
        }
        catch (IOException ex)
        {
            return WorkflowParseResult.Failed([$"Could not read workflow file: {ex.Message}"]);
        }
    }

    public WorkflowParseResult Parse(TextReader reader)
    {
        var errors = new List<string>();
        var yaml = new YamlStream();

        try
        {
            yaml.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return WorkflowParseResult.Failed([$"YAML could not be parsed: {ex.Message}"]);
        }

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return WorkflowParseResult.Failed(["Workflow file must contain a YAML mapping at the root."]);
        }

        AddUnknownKeyErrors(errors, root, TopLevelKeys, "workflow");

        var name = ReadRequiredScalar(errors, root, "name", "workflow.name");
        var env = ReadOptionalStringMap(errors, root, "env", "workflow.env");
        var jobs = ReadJobs(errors, root);

        if (errors.Count > 0)
        {
            return WorkflowParseResult.Failed(errors);
        }

        return WorkflowParseResult.Parsed(new WorkflowDocument(name!, env, jobs));
    }

    private static IReadOnlyDictionary<string, WorkflowJob> ReadJobs(List<string> errors, YamlMappingNode root)
    {
        if (!TryGet(root, "jobs", out var jobsNode))
        {
            errors.Add("workflow.jobs is required.");
            return new Dictionary<string, WorkflowJob>();
        }

        if (jobsNode is not YamlMappingNode jobsMap)
        {
            errors.Add("workflow.jobs must be a mapping.");
            return new Dictionary<string, WorkflowJob>();
        }

        if (jobsMap.Children.Count == 0)
        {
            errors.Add("workflow.jobs must contain at least one job.");
            return new Dictionary<string, WorkflowJob>();
        }

        var jobs = new Dictionary<string, WorkflowJob>(StringComparer.Ordinal);

        foreach (var (keyNode, valueNode) in jobsMap.Children)
        {
            var jobName = ReadMapKey(errors, keyNode, "workflow.jobs");
            if (jobName is null)
            {
                continue;
            }

            if (valueNode is not YamlMappingNode jobMap)
            {
                errors.Add($"workflow.jobs.{jobName} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, jobMap, JobKeys, $"workflow.jobs.{jobName}");

            var needs = ReadNeeds(errors, jobMap, $"workflow.jobs.{jobName}.needs");
            var condition = ReadOptionalScalar(errors, jobMap, "if", $"workflow.jobs.{jobName}.if");
            var runsOn = ReadRequiredScalar(errors, jobMap, "runs-on", $"workflow.jobs.{jobName}.runs-on");
            var outputs = ReadOptionalStringMap(errors, jobMap, "outputs", $"workflow.jobs.{jobName}.outputs");
            var artifacts = ReadArtifacts(errors, jobMap, jobName);
            var steps = ReadSteps(errors, jobMap, jobName);

            if (runsOn is not null)
            {
                jobs[jobName] = new WorkflowJob(jobName, needs, condition, runsOn, outputs, artifacts, steps);
            }
        }

        ValidateNeeds(errors, jobs);
        ValidateConditions(errors, jobs);

        return jobs;
    }

    private static IReadOnlyList<string> ReadNeeds(List<string> errors, YamlMappingNode jobMap, string path)
    {
        if (!TryGet(jobMap, "needs", out var needsNode))
        {
            return [];
        }

        if (needsNode is YamlScalarNode scalar)
        {
            var value = ReadScalarValue(errors, scalar, path);
            return value is null ? [] : [value];
        }

        if (needsNode is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a string or a list of strings.");
            return [];
        }

        var needs = new List<string>();

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlScalarNode item)
            {
                errors.Add($"{path}[{index}] must be a string.");
                continue;
            }

            var value = ReadScalarValue(errors, item, $"{path}[{index}]");
            if (value is not null)
            {
                needs.Add(value);
            }
        }

        return needs;
    }

    private static IReadOnlyList<WorkflowArtifact> ReadArtifacts(List<string> errors, YamlMappingNode jobMap, string jobName)
    {
        var path = $"workflow.jobs.{jobName}.artifacts";

        if (!TryGet(jobMap, "artifacts", out var artifactsNode))
        {
            return [];
        }

        if (artifactsNode is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a list.");
            return [];
        }

        var artifacts = new List<WorkflowArtifact>();

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var itemPath = $"{path}[{index}]";

            if (sequence.Children[index] is not YamlMappingNode artifactMap)
            {
                errors.Add($"{itemPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, artifactMap, ArtifactKeys, itemPath);

            var name = ReadRequiredScalar(errors, artifactMap, "name", $"{itemPath}.name");
            var artifactPath = ReadRequiredScalar(errors, artifactMap, "path", $"{itemPath}.path");

            if (name is not null && artifactPath is not null)
            {
                artifacts.Add(new WorkflowArtifact(name, artifactPath));
            }
        }

        return artifacts;
    }

    private static IReadOnlyList<WorkflowStep> ReadSteps(List<string> errors, YamlMappingNode jobMap, string jobName)
    {
        var path = $"workflow.jobs.{jobName}.steps";

        if (!TryGet(jobMap, "steps", out var stepsNode))
        {
            errors.Add($"{path} is required.");
            return [];
        }

        if (stepsNode is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a list.");
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            errors.Add($"{path} must contain at least one step.");
            return [];
        }

        var steps = new List<WorkflowStep>();

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var itemPath = $"{path}[{index}]";

            if (sequence.Children[index] is not YamlMappingNode stepMap)
            {
                errors.Add($"{itemPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, stepMap, StepKeys, itemPath);

            var name = ReadRequiredScalar(errors, stepMap, "name", $"{itemPath}.name");
            var run = ReadOptionalScalar(errors, stepMap, "run", $"{itemPath}.run");
            var uses = ReadOptionalScalar(errors, stepMap, "uses", $"{itemPath}.uses");

            if (run is null && uses is null)
            {
                errors.Add($"{itemPath} must define run or uses.");
            }

            if (run is not null && uses is not null)
            {
                errors.Add($"{itemPath} cannot define both run and uses.");
            }

            if (uses is not null && !ActionReference.IsSupportedLocalReference(uses))
            {
                errors.Add($"{itemPath}.uses supports only local action references starting with './'.");
            }

            if (name is not null)
            {
                steps.Add(new WorkflowStep(name, run, uses));
            }
        }

        return steps;
    }

    private static IReadOnlyDictionary<string, string> ReadOptionalStringMap(
        List<string> errors,
        YamlMappingNode map,
        string key,
        string path)
    {
        if (!TryGet(map, key, out var node))
        {
            return new Dictionary<string, string>();
        }

        if (node is not YamlMappingNode valueMap)
        {
            errors.Add($"{path} must be a mapping.");
            return new Dictionary<string, string>();
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (keyNode, valueNode) in valueMap.Children)
        {
            var name = ReadMapKey(errors, keyNode, path);
            if (name is null)
            {
                continue;
            }

            if (valueNode is not YamlScalarNode scalar)
            {
                errors.Add($"{path}.{name} must be a scalar value.");
                continue;
            }

            var value = ReadScalarValue(errors, scalar, $"{path}.{name}");
            if (value is not null)
            {
                values[name] = value;
            }
        }

        return values;
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

        return ReadScalarValue(errors, scalar, path);
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

        return ReadScalarValue(errors, scalar, path);
    }

    private static string? ReadScalarValue(List<string> errors, YamlScalarNode scalar, string path)
    {
        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            errors.Add($"{path} cannot be empty.");
            return null;
        }

        return scalar.Value;
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

    private static void ValidateNeeds(List<string> errors, IReadOnlyDictionary<string, WorkflowJob> jobs)
    {
        foreach (var job in jobs.Values)
        {
            foreach (var neededJob in job.Needs)
            {
                if (!jobs.ContainsKey(neededJob))
                {
                    errors.Add($"workflow.jobs.{job.Name}.needs references unknown job '{neededJob}'.");
                }
            }
        }
    }

    private static void ValidateConditions(List<string> errors, IReadOnlyDictionary<string, WorkflowJob> jobs)
    {
        foreach (var job in jobs.Values)
        {
            if (job.If is null)
            {
                continue;
            }

            if (!WorkflowConditionExpression.TryParse(job.If, out var condition))
            {
                errors.Add($"workflow.jobs.{job.Name}.if uses an unsupported expression.");
                continue;
            }

            var referencedJob = condition!.ReferencedJob;
            if (!jobs.ContainsKey(referencedJob))
            {
                errors.Add($"workflow.jobs.{job.Name}.if references unknown job '{referencedJob}'.");
                continue;
            }

            if (!job.Needs.Contains(referencedJob, StringComparer.Ordinal))
            {
                errors.Add($"workflow.jobs.{job.Name}.if references needs.{referencedJob}, but '{referencedJob}' is not declared in workflow.jobs.{job.Name}.needs.");
            }
        }
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
