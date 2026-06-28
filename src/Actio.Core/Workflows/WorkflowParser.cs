using Actio.Core.Actions;
using YamlDotNet.RepresentationModel;

namespace Actio.Core.Workflows;

public sealed partial class WorkflowParser
{
    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "name",
        "run-name",
        "on",
        "permissions",
        "env",
        "defaults",
        "concurrency",
        "jobs"
    };

    private static readonly HashSet<string> JobKeys = new(StringComparer.Ordinal)
    {
        "name",
        "needs",
        "if",
        "runs-on",
        "env",
        "defaults",
        "timeout-minutes",
        "continue-on-error",
        "concurrency",
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

    private static readonly HashSet<string> TriggerConfigurationKeys = new(StringComparer.Ordinal)
    {
        "types",
        "branches",
        "branches-ignore",
        "tags",
        "tags-ignore",
        "paths",
        "paths-ignore",
        "workflows",
        "cron",
        "inputs",
        "secrets",
        "outputs"
    };

    private static readonly HashSet<string> WorkflowDispatchInputKeys = new(StringComparer.Ordinal)
    {
        "description",
        "required",
        "default",
        "type",
        "options"
    };

    private static readonly HashSet<string> WorkflowDispatchInputTypes = new(StringComparer.Ordinal)
    {
        "boolean",
        "choice",
        "number",
        "environment",
        "string"
    };

    private static readonly HashSet<string> ScheduleKeys = new(StringComparer.Ordinal)
    {
        "cron"
    };

    private static readonly HashSet<string> DefaultsKeys = new(StringComparer.Ordinal)
    {
        "run"
    };

    private static readonly HashSet<string> DefaultsRunKeys = new(StringComparer.Ordinal)
    {
        "shell",
        "working-directory"
    };

    private static readonly HashSet<string> SupportedDefaultShells = new(StringComparer.Ordinal)
    {
        "bash",
        "sh"
    };

    private static readonly HashSet<string> JobConcurrencyKeys = new(StringComparer.Ordinal)
    {
        "group",
        "cancel-in-progress"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> KnownActivityTypes =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["issues"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "opened",
                "edited",
                "deleted",
                "transferred",
                "pinned",
                "unpinned",
                "closed",
                "reopened",
                "assigned",
                "unassigned",
                "labeled",
                "unlabeled",
                "locked",
                "unlocked",
                "milestoned",
                "demilestoned"
            },
            ["pull_request"] = CreatePullRequestActivityTypes(),
            ["pull_request_target"] = CreatePullRequestActivityTypes(),
            ["workflow_run"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "requested",
                "in_progress",
                "completed"
            }
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
        var warnings = new List<string>();
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
        var triggers = ReadTriggers(errors, warnings, root);
        ValidateTopLevelCompatibilityFields(errors, warnings, root);

        var name = ReadRequiredScalar(errors, root, "name", "workflow.name");
        var env = ReadOptionalStringMap(errors, root, "env", "workflow.env");
        var defaults = ReadRunDefaults(errors, root, "defaults", "workflow.defaults");
        var jobs = ReadJobs(errors, warnings, root);
        ValidateConditions(errors, jobs, triggers);

        if (errors.Count > 0)
        {
            return WorkflowParseResult.Failed(errors, warnings);
        }

        return WorkflowParseResult.Parsed(new WorkflowDocument(name!, env, jobs, triggers, defaults), warnings);
    }

    private static IReadOnlyDictionary<string, WorkflowJob> ReadJobs(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode root)
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

            var displayName = ReadOptionalScalar(errors, jobMap, "name", $"workflow.jobs.{jobName}.name");
            var needs = ReadNeeds(errors, jobMap, $"workflow.jobs.{jobName}.needs");
            var condition = ReadOptionalScalar(errors, jobMap, "if", $"workflow.jobs.{jobName}.if");
            var runsOn = ReadRequiredScalar(errors, jobMap, "runs-on", $"workflow.jobs.{jobName}.runs-on");
            var env = ReadOptionalStringMap(errors, jobMap, "env", $"workflow.jobs.{jobName}.env");
            var defaults = ReadRunDefaults(errors, jobMap, "defaults", $"workflow.jobs.{jobName}.defaults");
            var timeoutMinutes = ReadOptionalPositiveInt(errors, jobMap, "timeout-minutes", $"workflow.jobs.{jobName}.timeout-minutes");
            var continueOnError = ReadOptionalBoolean(errors, jobMap, "continue-on-error", $"workflow.jobs.{jobName}.continue-on-error") ?? false;
            var concurrency = ReadJobConcurrency(errors, jobMap, jobName);
            var outputs = ReadOptionalStringMap(errors, jobMap, "outputs", $"workflow.jobs.{jobName}.outputs");
            var artifacts = ReadArtifacts(errors, jobMap, jobName);
            var steps = ReadSteps(errors, warnings, jobMap, jobName);

            if (runsOn is not null)
            {
                jobs[jobName] = new WorkflowJob(
                    jobName,
                    displayName,
                    needs,
                    condition,
                    runsOn,
                    env,
                    defaults,
                    timeoutMinutes,
                    continueOnError,
                    concurrency,
                    outputs,
                    artifacts,
                    steps);
            }
        }

        ValidateNeeds(errors, jobs);
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

    private static IReadOnlyList<WorkflowStep> ReadSteps(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode jobMap,
        string jobName)
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

            ValidateUsesReference(errors, warnings, itemPath, uses);

            if (name is not null)
            {
                steps.Add(new WorkflowStep(name, run, uses));
            }
        }

        return steps;
    }

    private static void ValidateUsesReference(
        List<string> errors,
        List<string> warnings,
        string itemPath,
        string? uses)
    {
        if (uses is null)
        {
            return;
        }

        if (!ActionReference.TryParse(uses, out var reference))
        {
            errors.Add($"{itemPath}.uses has unsupported action reference '{uses}'. Supported formats are './...', 'docker://...', and 'owner/repo[/path]@ref'.");
            return;
        }

        if (!reference!.IsRemote)
        {
            return;
        }

        if (reference.IsMutable)
        {
            warnings.Add(FormatMutableReferenceWarning(itemPath, reference));
        }
    }

    private static string FormatMutableReferenceWarning(string itemPath, ActionReference reference)
    {
        return reference.Kind switch
        {
            ActionReferenceKind.DockerImage => $"{itemPath}.uses uses mutable Docker image reference '{reference.Value}' ({reference.MutablePart}). Pin with an image digest such as docker://image@sha256:<digest> for safer reuse.",
            ActionReferenceKind.GitHubRepository => $"{itemPath}.uses uses mutable GitHub ref '{reference.MutablePart}' in '{reference.Value}'. Pin with a commit SHA for safer reuse.",
            _ => $"{itemPath}.uses uses a mutable external action reference '{reference.Value}'."
        };
    }

    private static void ValidateTopLevelCompatibilityFields(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode root)
    {
        ValidateCompatibilityScalar(
            errors,
            warnings,
            root,
            "run-name",
            "workflow.run-name",
            "workflow.run-name is accepted for GitHub Actions compatibility but Actio does not use it as the run display name yet.");
        ValidatePermissionsCompatibility(errors, warnings, root);
        ValidateCompatibilityStringOrMapping(
            errors,
            warnings,
            root,
            "concurrency",
            "workflow.concurrency",
            "workflow.concurrency is accepted for GitHub Actions compatibility but Actio does not enforce concurrency groups yet.");
    }

    private static IReadOnlyList<WorkflowTrigger> ReadTriggers(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode root)
    {
        if (!TryGet(root, "on", out var node))
        {
            return [];
        }

        var path = "workflow.on";
        var errorCount = errors.Count;
        var triggers = new List<WorkflowTrigger>();

        if (node is YamlScalarNode scalar)
        {
            var eventName = ReadScalarValue(errors, scalar, path);
            if (eventName is not null)
            {
                triggers.Add(new WorkflowTrigger(eventName, null));
                AddTriggerWarnings(warnings, eventName);
            }

            AddTriggerWarningIfValid(errors, errorCount, warnings);
            return triggers;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                if (sequence.Children[index] is not YamlScalarNode item)
                {
                    errors.Add($"{path}[{index}] must be a string.");
                    continue;
                }

                var eventName = ReadScalarValue(errors, item, $"{path}[{index}]");
                if (eventName is not null)
                {
                    triggers.Add(new WorkflowTrigger(eventName, null));
                    AddTriggerWarnings(warnings, eventName);
                }
            }

            AddTriggerWarningIfValid(errors, errorCount, warnings);
            return triggers;
        }

        if (node is YamlMappingNode map)
        {
            foreach (var (keyNode, valueNode) in map.Children)
            {
                var eventName = ReadMapKey(errors, keyNode, path);
                if (eventName is null)
                {
                    continue;
                }

                var configurationPath = $"{path}.{eventName}";
                var configuration = ReadTriggerConfiguration(errors, valueNode, configurationPath);
                var filters = ReadTriggerFilters(errors, valueNode, configurationPath);
                var dispatch = ReadWorkflowDispatch(errors, eventName, valueNode, configurationPath);
                var schedules = ReadWorkflowSchedules(errors, eventName, valueNode, configurationPath);
                var activityTypes = ReadActivityTypes(errors, warnings, eventName, valueNode, configurationPath);
                triggers.Add(new WorkflowTrigger(eventName, configuration, filters, dispatch, schedules, activityTypes));
                AddTriggerWarnings(warnings, eventName);
            }

            AddTriggerWarningIfValid(errors, errorCount, warnings);
            return triggers;
        }

        errors.Add($"{path} must be a string, a list, or a mapping.");
        return triggers;
    }

    private static WorkflowDispatch ReadWorkflowDispatch(
        List<string> errors,
        string eventName,
        YamlNode node,
        string path)
    {
        if (!string.Equals(eventName, "workflow_dispatch", StringComparison.Ordinal))
        {
            return WorkflowDispatch.Empty;
        }

        if (IsEmptyScalar(node))
        {
            return WorkflowDispatch.Empty;
        }

        if (node is not YamlMappingNode map)
        {
            errors.Add($"{path} must be a mapping when workflow_dispatch inputs are configured.");
            return WorkflowDispatch.Empty;
        }

        if (!TryGet(map, "inputs", out var inputsNode))
        {
            return WorkflowDispatch.Empty;
        }

        var inputsPath = $"{path}.inputs";
        if (inputsNode is not YamlMappingNode inputsMap)
        {
            errors.Add($"{inputsPath} must be a mapping.");
            return WorkflowDispatch.Empty;
        }

        var inputs = new Dictionary<string, WorkflowDispatchInput>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in inputsMap.Children)
        {
            var inputName = ReadMapKey(errors, keyNode, inputsPath);
            if (inputName is null)
            {
                continue;
            }

            var inputPath = $"{inputsPath}.{inputName}";
            if (valueNode is not YamlMappingNode inputMap)
            {
                errors.Add($"{inputPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, inputMap, WorkflowDispatchInputKeys, inputPath);

            var description = ReadOptionalScalar(errors, inputMap, "description", $"{inputPath}.description");
            var required = ReadOptionalBoolean(errors, inputMap, "required", $"{inputPath}.required") ?? false;
            var defaultValue = ReadOptionalScalar(errors, inputMap, "default", $"{inputPath}.default");
            var type = ReadOptionalScalar(errors, inputMap, "type", $"{inputPath}.type") ?? "string";
            var options = ReadOptionalStringList(errors, inputMap, "options", $"{inputPath}.options");

            if (!WorkflowDispatchInputTypes.Contains(type))
            {
                errors.Add($"{inputPath}.type must be one of boolean, choice, number, environment, or string.");
            }

            if (options.Count > 0 && !string.Equals(type, "choice", StringComparison.Ordinal))
            {
                errors.Add($"{inputPath}.options can only be used when type is choice.");
            }

            if (string.Equals(type, "choice", StringComparison.Ordinal) && options.Count == 0)
            {
                errors.Add($"{inputPath}.options is required when type is choice.");
            }

            inputs[inputName] = new WorkflowDispatchInput(inputName, description, required, defaultValue, type, options);
        }

        return new WorkflowDispatch(inputs);
    }

    private static IReadOnlyList<string> ReadActivityTypes(
        List<string> errors,
        List<string> warnings,
        string eventName,
        YamlNode node,
        string path)
    {
        if (node is not YamlMappingNode map)
        {
            return [];
        }

        var activityTypes = ReadOptionalStringList(errors, map, "types", $"{path}.types");
        if (activityTypes.Count == 0 || !KnownActivityTypes.TryGetValue(eventName, out var knownTypes))
        {
            return activityTypes;
        }

        foreach (var activityType in activityTypes)
        {
            if (!knownTypes.Contains(activityType))
            {
                warnings.Add($"{path}.types contains unknown activity type '{activityType}' for event '{eventName}'. Actio stores it as metadata but may not match it in future trigger evaluation.");
            }
        }

        return activityTypes;
    }

    private static IReadOnlyList<WorkflowSchedule> ReadWorkflowSchedules(
        List<string> errors,
        string eventName,
        YamlNode node,
        string path)
    {
        if (!string.Equals(eventName, "schedule", StringComparison.Ordinal))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a list of cron entries.");
            return [];
        }

        var schedules = new List<WorkflowSchedule>();
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            if (sequence.Children[index] is not YamlMappingNode map)
            {
                errors.Add($"{itemPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, map, ScheduleKeys, itemPath);
            var cron = ReadRequiredScalar(errors, map, "cron", $"{itemPath}.cron");
            if (cron is null)
            {
                continue;
            }

            if (!HasFiveCronFields(cron))
            {
                errors.Add($"{itemPath}.cron must contain five cron fields.");
                continue;
            }

            schedules.Add(new WorkflowSchedule(cron));
        }

        return schedules;
    }

    private static WorkflowTriggerValue? ReadTriggerConfiguration(
        List<string> errors,
        YamlNode node,
        string path)
    {
        if (IsEmptyScalar(node))
        {
            return null;
        }

        if (node is YamlScalarNode)
        {
            errors.Add($"{path} must be a mapping or a list.");
            return null;
        }

        if (node is YamlMappingNode map)
        {
            AddUnsupportedTriggerConfigurationKeyErrors(errors, map, path);
            return ReadTriggerValue(errors, map, path);
        }

        if (node is YamlSequenceNode sequence)
        {
            return ReadTriggerValue(errors, sequence, path);
        }

        errors.Add($"{path} must be a mapping or a list.");
        return null;
    }

    private static WorkflowTriggerFilters ReadTriggerFilters(
        List<string> errors,
        YamlNode node,
        string path)
    {
        if (node is not YamlMappingNode map)
        {
            return WorkflowTriggerFilters.Empty;
        }

        AddMutuallyExclusiveTriggerFilterErrors(errors, map, path, "branches", "branches-ignore");
        AddMutuallyExclusiveTriggerFilterErrors(errors, map, path, "tags", "tags-ignore");
        AddMutuallyExclusiveTriggerFilterErrors(errors, map, path, "paths", "paths-ignore");

        return new WorkflowTriggerFilters(
            ReadTriggerFilterPatterns(errors, map, "branches", path),
            ReadTriggerFilterPatterns(errors, map, "branches-ignore", path),
            ReadTriggerFilterPatterns(errors, map, "tags", path),
            ReadTriggerFilterPatterns(errors, map, "tags-ignore", path),
            ReadTriggerFilterPatterns(errors, map, "paths", path),
            ReadTriggerFilterPatterns(errors, map, "paths-ignore", path));
    }

    private static IReadOnlyList<string> ReadTriggerFilterPatterns(
        List<string> errors,
        YamlMappingNode map,
        string key,
        string path)
    {
        if (!TryGet(map, key, out var node))
        {
            return [];
        }

        var filterPath = $"{path}.{key}";

        if (node is YamlScalarNode scalar)
        {
            var value = ReadScalarValue(errors, scalar, filterPath);
            return value is null ? [] : [value];
        }

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{filterPath} must be a string or a list of strings.");
            return [];
        }

        var patterns = new List<string>();

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlScalarNode item)
            {
                errors.Add($"{filterPath}[{index}] must be a string.");
                continue;
            }

            var value = ReadScalarValue(errors, item, $"{filterPath}[{index}]");
            if (value is not null)
            {
                patterns.Add(value);
            }
        }

        return patterns;
    }

    private static void AddMutuallyExclusiveTriggerFilterErrors(
        List<string> errors,
        YamlMappingNode map,
        string path,
        string includeKey,
        string ignoreKey)
    {
        if (TryGet(map, includeKey, out _) && TryGet(map, ignoreKey, out _))
        {
            errors.Add($"{path} cannot define both {includeKey} and {ignoreKey}. Use ordered ! patterns in {includeKey} when both include and exclude behavior is needed.");
        }
    }

    private static WorkflowTriggerValue? ReadTriggerValue(List<string> errors, YamlNode node, string path)
    {
        if (node is YamlScalarNode scalar)
        {
            var value = ReadScalarValue(errors, scalar, path);
            return value is null ? null : WorkflowTriggerValue.Scalar(value);
        }

        if (node is YamlSequenceNode sequence)
        {
            var items = new List<WorkflowTriggerValue>();
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                var item = ReadTriggerValue(errors, sequence.Children[index], $"{path}[{index}]");
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return WorkflowTriggerValue.Sequence(items);
        }

        if (node is YamlMappingNode map)
        {
            var properties = new Dictionary<string, WorkflowTriggerValue>(StringComparer.Ordinal);
            foreach (var (keyNode, valueNode) in map.Children)
            {
                var name = ReadMapKey(errors, keyNode, path);
                if (name is null)
                {
                    continue;
                }

                var value = ReadTriggerValue(errors, valueNode, $"{path}.{name}");
                if (value is not null)
                {
                    properties[name] = value;
                }
            }

            return WorkflowTriggerValue.Mapping(properties);
        }

        errors.Add($"{path} must be a string, a list, or a mapping.");
        return null;
    }

    private static void AddUnsupportedTriggerConfigurationKeyErrors(
        List<string> errors,
        YamlMappingNode map,
        string path)
    {
        foreach (var keyNode in map.Children.Keys)
        {
            if (keyNode is not YamlScalarNode scalar || scalar.Value is null)
            {
                continue;
            }

            if (!TriggerConfigurationKeys.Contains(scalar.Value))
            {
                errors.Add($"{path}.{scalar.Value} is not supported in workflow trigger configuration.");
            }
        }
    }

    private static void AddTriggerSecurityWarnings(List<string> warnings, string eventName)
    {
        if (string.Equals(eventName, "pull_request_target", StringComparison.Ordinal))
        {
            warnings.Add("workflow.on.pull_request_target is security-sensitive in GitHub Actions. Actio stores it as local trigger metadata only and does not model fork trust, hosted tokens, or permission elevation.");
        }
    }

    private static void AddTriggerWarnings(List<string> warnings, string eventName)
    {
        AddTriggerSecurityWarnings(warnings, eventName);

        if (string.Equals(eventName, "schedule", StringComparison.Ordinal))
        {
            warnings.Add("workflow.on.schedule is parsed as trigger metadata. Actio does not run schedules itself; use the operating system scheduler to invoke actio locally.");
        }

        if (string.Equals(eventName, "repository_dispatch", StringComparison.Ordinal))
        {
            warnings.Add("workflow.on.repository_dispatch is parsed as trigger metadata. Actio does not receive GitHub repository dispatch webhooks.");
        }
    }

    private static void AddTriggerWarningIfValid(
        IReadOnlyCollection<string> errors,
        int originalErrorCount,
        List<string> warnings)
    {
        AddWarningIfNoNewErrors(
            errors,
            originalErrorCount,
            warnings,
            "workflow.on is parsed as trigger metadata, but Actio still runs workflows only when invoked locally.");
    }

    private static void ValidatePermissionsCompatibility(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode root)
    {
        if (!TryGet(root, "permissions", out var node))
        {
            return;
        }

        var path = "workflow.permissions";
        if (node is YamlScalarNode scalar)
        {
            if (ReadScalarValue(errors, scalar, path) is not null)
            {
                warnings.Add("workflow.permissions is accepted for GitHub Actions compatibility but Actio does not create or enforce GITHUB_TOKEN permissions.");
            }

            return;
        }

        if (node is YamlMappingNode map)
        {
            var errorCount = errors.Count;
            ValidateScalarMap(errors, map, path);
            AddWarningIfNoNewErrors(errors, errorCount, warnings, "workflow.permissions is accepted for GitHub Actions compatibility but Actio does not create or enforce GITHUB_TOKEN permissions.");
            return;
        }

        errors.Add($"{path} must be a string or a mapping.");
    }

    private static void ValidateCompatibilityScalar(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode root,
        string key,
        string path,
        string warning)
    {
        if (!TryGet(root, key, out var node))
        {
            return;
        }

        if (node is not YamlScalarNode scalar)
        {
            errors.Add($"{path} must be a string.");
            return;
        }

        if (ReadScalarValue(errors, scalar, path) is not null)
        {
            warnings.Add(warning);
        }
    }

    private static void ValidateCompatibilityStringOrMapping(
        List<string> errors,
        List<string> warnings,
        YamlMappingNode root,
        string key,
        string path,
        string warning)
    {
        if (!TryGet(root, key, out var node))
        {
            return;
        }

        if (node is YamlScalarNode scalar)
        {
            if (ReadScalarValue(errors, scalar, path) is not null)
            {
                warnings.Add(warning);
            }

            return;
        }

        if (node is YamlMappingNode map)
        {
            var errorCount = errors.Count;
            ValidateScalarKeys(errors, map, path);
            AddWarningIfNoNewErrors(errors, errorCount, warnings, warning);
            return;
        }

        errors.Add($"{path} must be a string or a mapping.");
    }

    private static void ValidateScalarKeys(List<string> errors, YamlMappingNode map, string path)
    {
        foreach (var keyNode in map.Children.Keys)
        {
            ReadMapKey(errors, keyNode, path);
        }
    }

    private static void ValidateScalarMap(List<string> errors, YamlMappingNode map, string path)
    {
        foreach (var (keyNode, valueNode) in map.Children)
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

            ReadScalarValue(errors, scalar, $"{path}.{name}");
        }
    }

    private static void AddWarningIfNoNewErrors(
        IReadOnlyCollection<string> errors,
        int originalErrorCount,
        List<string> warnings,
        string warning)
    {
        if (errors.Count == originalErrorCount)
        {
            warnings.Add(warning);
        }
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

    private static WorkflowRunDefaults ReadRunDefaults(
        List<string> errors,
        YamlMappingNode map,
        string key,
        string path)
    {
        if (!TryGet(map, key, out var node))
        {
            return WorkflowRunDefaults.Empty;
        }

        if (node is not YamlMappingNode defaultsMap)
        {
            errors.Add($"{path} must be a mapping.");
            return WorkflowRunDefaults.Empty;
        }

        AddUnknownKeyErrors(errors, defaultsMap, DefaultsKeys, path);
        if (!TryGet(defaultsMap, "run", out var runNode))
        {
            return WorkflowRunDefaults.Empty;
        }

        var runPath = $"{path}.run";
        if (runNode is not YamlMappingNode runMap)
        {
            errors.Add($"{runPath} must be a mapping.");
            return WorkflowRunDefaults.Empty;
        }

        AddUnknownKeyErrors(errors, runMap, DefaultsRunKeys, runPath);
        var shell = ReadOptionalScalar(errors, runMap, "shell", $"{runPath}.shell");
        var workingDirectory = ReadOptionalScalar(errors, runMap, "working-directory", $"{runPath}.working-directory");

        if (shell is not null && !SupportedDefaultShells.Contains(shell))
        {
            errors.Add($"{runPath}.shell must be bash or sh.");
        }

        if (workingDirectory is not null && !IsSafeRelativePath(workingDirectory))
        {
            errors.Add($"{runPath}.working-directory must be a relative path inside the workspace.");
        }

        return new WorkflowRunDefaults(shell, workingDirectory);
    }

    private static int? ReadOptionalPositiveInt(
        List<string> errors,
        YamlMappingNode map,
        string key,
        string path)
    {
        var value = ReadOptionalScalar(errors, map, key, path);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, out var result) && result > 0)
        {
            return result;
        }

        errors.Add($"{path} must be a positive integer.");
        return null;
    }

    private static WorkflowJobConcurrency? ReadJobConcurrency(
        List<string> errors,
        YamlMappingNode jobMap,
        string jobName)
    {
        if (!TryGet(jobMap, "concurrency", out var node))
        {
            return null;
        }

        var path = $"workflow.jobs.{jobName}.concurrency";
        if (node is YamlScalarNode scalar)
        {
            var group = ReadScalarValue(errors, scalar, path);
            return group is null ? null : new WorkflowJobConcurrency(group, false);
        }

        if (node is not YamlMappingNode concurrencyMap)
        {
            errors.Add($"{path} must be a string or a mapping.");
            return null;
        }

        AddUnknownKeyErrors(errors, concurrencyMap, JobConcurrencyKeys, path);
        var groupValue = ReadRequiredScalar(errors, concurrencyMap, "group", $"{path}.group");
        var cancelInProgress =
            ReadOptionalBoolean(errors, concurrencyMap, "cancel-in-progress", $"{path}.cancel-in-progress") ?? false;

        return groupValue is null
            ? null
            : new WorkflowJobConcurrency(groupValue, cancelInProgress);
    }

    private static IReadOnlyList<string> ReadOptionalStringList(
        List<string> errors,
        YamlMappingNode map,
        string key,
        string path)
    {
        if (!TryGet(map, key, out var node))
        {
            return [];
        }

        if (node is YamlScalarNode scalar)
        {
            var value = ReadScalarValue(errors, scalar, path);
            return value is null ? [] : [value];
        }

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a string or a list of strings.");
            return [];
        }

        var values = new List<string>();
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
                values.Add(value);
            }
        }

        return values;
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

    private static bool IsEmptyScalar(YamlNode node)
        => node is YamlScalarNode scalar && string.IsNullOrWhiteSpace(scalar.Value);

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

    private static void ValidateConditions(
        List<string> errors,
        IReadOnlyDictionary<string, WorkflowJob> jobs,
        IReadOnlyList<WorkflowTrigger> triggers)
    {
        var dispatchInputNames = triggers
            .FirstOrDefault(trigger => string.Equals(trigger.EventName, "workflow_dispatch", StringComparison.Ordinal))?
            .Dispatch
            .Inputs
            .Keys
            .ToHashSet(StringComparer.Ordinal) ?? [];

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

            if (condition!.Kind == WorkflowConditionExpressionKind.Input)
            {
                if (!dispatchInputNames.Contains(condition.Name))
                {
                    errors.Add($"workflow.jobs.{job.Name}.if references inputs.{condition.Name}, but workflow.on.workflow_dispatch.inputs.{condition.Name} is not declared.");
                }

                continue;
            }

            if (condition.Kind == WorkflowConditionExpressionKind.EventPayload)
            {
                continue;
            }

            var referencedJob = condition.ReferencedJob!;
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

    private static bool HasFiveCronFields(string cron)
        => cron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 5;

    private static bool IsSafeRelativePath(string path)
    {
        if (Path.IsPathRooted(path) || path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        return !path
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }

    private static IReadOnlySet<string> CreatePullRequestActivityTypes()
    {
        return new HashSet<string>(StringComparer.Ordinal)
        {
            "assigned",
            "unassigned",
            "labeled",
            "unlabeled",
            "opened",
            "edited",
            "closed",
            "reopened",
            "synchronize",
            "converted_to_draft",
            "ready_for_review",
            "locked",
            "unlocked",
            "review_requested",
            "review_request_removed",
            "auto_merge_enabled",
            "auto_merge_disabled"
        };
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
