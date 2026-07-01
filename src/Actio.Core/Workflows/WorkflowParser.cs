using Actio.Core.Actions;
using Actio.Core.Expressions;
using System.Globalization;
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
        "container",
        "services",
        "timeout-minutes",
        "continue-on-error",
        "concurrency",
        "strategy",
        "uses",
        "with",
        "secrets",
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
        "id",
        "name",
        "if",
        "run",
        "uses",
        "env",
        "shell",
        "working-directory",
        "timeout-minutes",
        "continue-on-error",
        "with"
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

    private static readonly HashSet<string> WorkflowCallKeys = new(StringComparer.Ordinal)
    {
        "inputs",
        "secrets",
        "outputs"
    };

    private static readonly HashSet<string> WorkflowCallInputKeys = new(StringComparer.Ordinal)
    {
        "description",
        "required",
        "default",
        "type"
    };

    private static readonly HashSet<string> WorkflowCallInputTypes = new(StringComparer.Ordinal)
    {
        "boolean",
        "number",
        "string"
    };

    private static readonly HashSet<string> WorkflowCallSecretKeys = new(StringComparer.Ordinal)
    {
        "description",
        "required"
    };

    private static readonly HashSet<string> WorkflowCallOutputKeys = new(StringComparer.Ordinal)
    {
        "description",
        "value"
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

    private static readonly HashSet<string> JobContainerKeys = new(StringComparer.Ordinal)
    {
        "image",
        "env",
        "ports",
        "volumes",
        "options"
    };

    private static readonly HashSet<string> JobServiceKeys = new(StringComparer.Ordinal)
    {
        "image",
        "env",
        "ports",
        "volumes",
        "options"
    };

    private static readonly HashSet<string> SupportedContainerOptionsWithValues = new(StringComparer.Ordinal)
    {
        "--add-host",
        "--cpus",
        "--dns",
        "--dns-search",
        "--health-cmd",
        "--health-interval",
        "--health-retries",
        "--health-start-period",
        "--health-timeout",
        "--hostname",
        "--memory",
        "--memory-reservation",
        "--memory-swap",
        "--shm-size",
        "--ulimit"
    };

    private static readonly HashSet<string> SupportedContainerOptionsWithoutValues = new(StringComparer.Ordinal)
    {
        "--init",
        "--no-healthcheck"
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

    private static readonly HashSet<string> JobStrategyKeys = new(StringComparer.Ordinal)
    {
        "matrix",
        "fail-fast",
        "max-parallel"
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
            var uses = ReadOptionalScalar(errors, jobMap, "uses", $"workflow.jobs.{jobName}.uses");
            var runsOn = uses is null
                ? ReadRequiredScalar(errors, jobMap, "runs-on", $"workflow.jobs.{jobName}.runs-on")
                : null;
            var env = ReadOptionalStringMap(errors, jobMap, "env", $"workflow.jobs.{jobName}.env");
            var defaults = ReadRunDefaults(errors, jobMap, "defaults", $"workflow.jobs.{jobName}.defaults");
            var container = ReadJobContainer(errors, jobMap, jobName);
            var services = ReadJobServices(errors, jobMap, jobName);
            var timeoutMinutes = ReadOptionalPositiveInt(errors, jobMap, "timeout-minutes", $"workflow.jobs.{jobName}.timeout-minutes");
            var continueOnError = ReadOptionalBoolean(errors, jobMap, "continue-on-error", $"workflow.jobs.{jobName}.continue-on-error") ?? false;
            var concurrency = ReadJobConcurrency(errors, jobMap, jobName);
            var strategy = ReadJobStrategy(errors, jobMap, jobName);
            var outputs = ReadOptionalStringMap(errors, jobMap, "outputs", $"workflow.jobs.{jobName}.outputs");
            var artifacts = ReadArtifacts(errors, jobMap, jobName);
            var steps = uses is null ? ReadSteps(errors, warnings, jobMap, jobName) : [];
            var with = ReadOptionalStringMap(errors, jobMap, "with", $"workflow.jobs.{jobName}.with");
            var secrets = ReadJobSecrets(errors, jobMap, jobName);

            ValidateJobCallShape(errors, jobMap, jobName, uses, with, secrets);

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
                    strategy,
                    outputs,
                    artifacts,
                    steps,
                    container,
                    services);
            }
            else if (uses is not null)
            {
                jobs[jobName] = new WorkflowJob(
                    jobName,
                    displayName,
                    needs,
                    condition,
                    "reusable-workflow",
                    new Dictionary<string, string>(),
                    WorkflowRunDefaults.Empty,
                    null,
                    continueOnError,
                    null,
                    WorkflowJobStrategy.Empty,
                    new Dictionary<string, string>(),
                    [],
                    [],
                    call: new WorkflowJobCall(uses, with, secrets));
            }
        }

        ValidateNeeds(errors, jobs);
        return jobs;
    }

    private static IReadOnlyDictionary<string, string> ReadJobSecrets(
        List<string> errors,
        YamlMappingNode jobMap,
        string jobName)
    {
        var path = $"workflow.jobs.{jobName}.secrets";
        if (!TryGet(jobMap, "secrets", out var secretsNode))
        {
            return new Dictionary<string, string>();
        }

        if (secretsNode is YamlScalarNode scalar)
        {
            var value = ReadScalarValue(errors, scalar, path);
            errors.Add(string.Equals(value, "inherit", StringComparison.Ordinal)
                ? $"{path}: inherit is not supported for local reusable workflow calls."
                : $"{path} must be a mapping.");
            return new Dictionary<string, string>();
        }

        if (secretsNode is not YamlMappingNode secretsMap)
        {
            errors.Add($"{path} must be a mapping.");
            return new Dictionary<string, string>();
        }

        return ReadStringMapEntries(errors, secretsMap, path);
    }

    private static void ValidateJobCallShape(
        List<string> errors,
        YamlMappingNode jobMap,
        string jobName,
        string? uses,
        IReadOnlyDictionary<string, string> with,
        IReadOnlyDictionary<string, string> secrets)
    {
        if (uses is null)
        {
            if (with.Count > 0)
            {
                errors.Add($"workflow.jobs.{jobName}.with is supported only on reusable workflow call jobs.");
            }

            if (secrets.Count > 0)
            {
                errors.Add($"workflow.jobs.{jobName}.secrets is supported only on reusable workflow call jobs.");
            }

            return;
        }

        var callPath = $"workflow.jobs.{jobName}";
        foreach (var key in new[]
        {
            "runs-on",
            "env",
            "defaults",
            "container",
            "services",
            "timeout-minutes",
            "concurrency",
            "strategy",
            "outputs",
            "artifacts",
            "steps"
        })
        {
            if (TryGet(jobMap, key, out _))
            {
                errors.Add($"{callPath}.{key} cannot be used when the job calls a reusable workflow with uses.");
            }
        }

        ValidateLocalReusableWorkflowReference(errors, uses, $"{callPath}.uses");
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
        var stepIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var itemPath = $"{path}[{index}]";

            if (sequence.Children[index] is not YamlMappingNode stepMap)
            {
                errors.Add($"{itemPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, stepMap, StepKeys, itemPath);

            var id = ReadOptionalScalar(errors, stepMap, "id", $"{itemPath}.id");
            var name = ReadRequiredScalar(errors, stepMap, "name", $"{itemPath}.name");
            var condition = ReadOptionalScalar(errors, stepMap, "if", $"{itemPath}.if");
            var run = ReadOptionalScalar(errors, stepMap, "run", $"{itemPath}.run");
            var uses = ReadOptionalScalar(errors, stepMap, "uses", $"{itemPath}.uses");
            var env = ReadOptionalStringMap(errors, stepMap, "env", $"{itemPath}.env");
            var shell = ReadOptionalScalar(errors, stepMap, "shell", $"{itemPath}.shell");
            var workingDirectory = ReadOptionalScalar(errors, stepMap, "working-directory", $"{itemPath}.working-directory");
            var timeoutMinutes = ReadOptionalPositiveInt(errors, stepMap, "timeout-minutes", $"{itemPath}.timeout-minutes");
            var continueOnError = ReadOptionalBoolean(errors, stepMap, "continue-on-error", $"{itemPath}.continue-on-error") ?? false;
            var with = ReadOptionalStringMap(errors, stepMap, "with", $"{itemPath}.with");

            ValidateStepId(errors, stepIds, id, $"{itemPath}.id");

            if (shell is not null && !SupportedDefaultShells.Contains(shell))
            {
                errors.Add($"{itemPath}.shell must be bash or sh.");
            }

            if (workingDirectory is not null && !IsSafeRelativePath(workingDirectory))
            {
                errors.Add($"{itemPath}.working-directory must be a relative path inside the workspace.");
            }

            if (run is null && uses is null)
            {
                errors.Add($"{itemPath} must define run or uses.");
            }

            if (run is not null && uses is not null)
            {
                errors.Add($"{itemPath} cannot define both run and uses.");
            }

            if (uses is not null && shell is not null)
            {
                errors.Add($"{itemPath}.shell is supported only for run steps.");
            }

            if (uses is not null && workingDirectory is not null)
            {
                errors.Add($"{itemPath}.working-directory is supported only for run steps.");
            }

            if (run is not null && with.Count > 0)
            {
                errors.Add($"{itemPath}.with is supported only for uses steps.");
            }

            ValidateUsesReference(errors, warnings, itemPath, uses);
            ValidateCheckoutShimInputs(errors, itemPath, uses, with);

            if (name is not null)
            {
                steps.Add(new WorkflowStep(name, run, uses, id, env, shell, workingDirectory, condition, timeoutMinutes, continueOnError, with));
            }
        }

        return steps;
    }

    private static void ValidateStepId(
        List<string> errors,
        HashSet<string> stepIds,
        string? id,
        string path)
    {
        if (id is null)
        {
            return;
        }

        if (!IsValidStepId(id))
        {
            errors.Add($"{path} must contain only letters, numbers, '_', and '-', and must start with a letter or '_'.");
            return;
        }

        if (!stepIds.Add(id))
        {
            errors.Add($"{path} '{id}' is already used in this job.");
        }
    }

    private static bool IsValidStepId(string id)
    {
        return id.Length > 0 &&
            (char.IsAsciiLetter(id[0]) || id[0] == '_') &&
            id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
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

    private static void ValidateLocalReusableWorkflowReference(
        List<string> errors,
        string uses,
        string path)
    {
        var normalized = uses.Replace('\\', '/');
        if (!normalized.StartsWith("./", StringComparison.Ordinal))
        {
            errors.Add($"{path} supports only local reusable workflow references in this milestone.");
            return;
        }

        if (!normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path} must reference a .yml or .yaml workflow file.");
        }

        if (!normalized.StartsWith("./.workflows/", StringComparison.Ordinal) &&
            !normalized.StartsWith("./.github/workflows/", StringComparison.Ordinal))
        {
            errors.Add($"{path} must reference a workflow under .workflows/ or .github/workflows/.");
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
                var configuration = ReadTriggerConfiguration(errors, eventName, valueNode, configurationPath);
                var filters = ReadTriggerFilters(errors, valueNode, configurationPath);
                var dispatch = ReadWorkflowDispatch(errors, eventName, valueNode, configurationPath);
                var call = ReadWorkflowCall(errors, eventName, valueNode, configurationPath);
                var schedules = ReadWorkflowSchedules(errors, eventName, valueNode, configurationPath);
                var activityTypes = ReadActivityTypes(errors, warnings, eventName, valueNode, configurationPath);
                triggers.Add(new WorkflowTrigger(eventName, configuration, filters, dispatch, call, schedules, activityTypes));
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

    private static WorkflowCall ReadWorkflowCall(
        List<string> errors,
        string eventName,
        YamlNode node,
        string path)
    {
        if (!string.Equals(eventName, "workflow_call", StringComparison.Ordinal))
        {
            return WorkflowCall.Empty;
        }

        if (IsEmptyScalar(node))
        {
            return WorkflowCall.Empty;
        }

        if (node is not YamlMappingNode map)
        {
            errors.Add($"{path} must be a mapping when workflow_call inputs, secrets, or outputs are configured.");
            return WorkflowCall.Empty;
        }

        return new WorkflowCall(
            ReadWorkflowCallInputs(errors, map, path),
            ReadWorkflowCallSecrets(errors, map, path),
            ReadWorkflowCallOutputs(errors, map, path));
    }

    private static IReadOnlyDictionary<string, WorkflowCallInput> ReadWorkflowCallInputs(
        List<string> errors,
        YamlMappingNode map,
        string path)
    {
        if (!TryGet(map, "inputs", out var inputsNode))
        {
            return new Dictionary<string, WorkflowCallInput>();
        }

        var inputsPath = $"{path}.inputs";
        if (inputsNode is not YamlMappingNode inputsMap)
        {
            errors.Add($"{inputsPath} must be a mapping.");
            return new Dictionary<string, WorkflowCallInput>();
        }

        var inputs = new Dictionary<string, WorkflowCallInput>(StringComparer.Ordinal);
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

            AddUnknownKeyErrors(errors, inputMap, WorkflowCallInputKeys, inputPath);

            var description = ReadOptionalScalar(errors, inputMap, "description", $"{inputPath}.description");
            var required = ReadOptionalBoolean(errors, inputMap, "required", $"{inputPath}.required") ?? false;
            var defaultValue = ReadOptionalScalar(errors, inputMap, "default", $"{inputPath}.default");
            var type = ReadRequiredScalar(errors, inputMap, "type", $"{inputPath}.type");

            if (type is not null)
            {
                if (!WorkflowCallInputTypes.Contains(type))
                {
                    errors.Add($"{inputPath}.type must be one of boolean, number, or string.");
                }

                ValidateWorkflowCallInputDefault(errors, inputPath, type, defaultValue);
            }

            inputs[inputName] = new WorkflowCallInput(inputName, description, required, defaultValue, type ?? string.Empty);
        }

        return inputs;
    }

    private static void ValidateWorkflowCallInputDefault(
        List<string> errors,
        string inputPath,
        string type,
        string? defaultValue)
    {
        if (defaultValue is null)
        {
            return;
        }

        if (string.Equals(type, "boolean", StringComparison.Ordinal)
            && !bool.TryParse(defaultValue, out _))
        {
            errors.Add($"{inputPath}.default must be true or false when type is boolean.");
            return;
        }

        if (string.Equals(type, "number", StringComparison.Ordinal)
            && !decimal.TryParse(defaultValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            errors.Add($"{inputPath}.default must be a number when type is number.");
        }
    }

    private static IReadOnlyDictionary<string, WorkflowCallSecret> ReadWorkflowCallSecrets(
        List<string> errors,
        YamlMappingNode map,
        string path)
    {
        if (!TryGet(map, "secrets", out var secretsNode))
        {
            return new Dictionary<string, WorkflowCallSecret>();
        }

        var secretsPath = $"{path}.secrets";
        if (secretsNode is not YamlMappingNode secretsMap)
        {
            errors.Add($"{secretsPath} must be a mapping.");
            return new Dictionary<string, WorkflowCallSecret>();
        }

        var secrets = new Dictionary<string, WorkflowCallSecret>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in secretsMap.Children)
        {
            var secretName = ReadMapKey(errors, keyNode, secretsPath);
            if (secretName is null)
            {
                continue;
            }

            var secretPath = $"{secretsPath}.{secretName}";
            if (valueNode is not YamlMappingNode secretMap)
            {
                errors.Add($"{secretPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, secretMap, WorkflowCallSecretKeys, secretPath);

            var description = ReadOptionalScalar(errors, secretMap, "description", $"{secretPath}.description");
            var required = ReadOptionalBoolean(errors, secretMap, "required", $"{secretPath}.required") ?? false;

            secrets[secretName] = new WorkflowCallSecret(secretName, description, required);
        }

        return secrets;
    }

    private static IReadOnlyDictionary<string, WorkflowCallOutput> ReadWorkflowCallOutputs(
        List<string> errors,
        YamlMappingNode map,
        string path)
    {
        if (!TryGet(map, "outputs", out var outputsNode))
        {
            return new Dictionary<string, WorkflowCallOutput>();
        }

        var outputsPath = $"{path}.outputs";
        if (outputsNode is not YamlMappingNode outputsMap)
        {
            errors.Add($"{outputsPath} must be a mapping.");
            return new Dictionary<string, WorkflowCallOutput>();
        }

        var outputs = new Dictionary<string, WorkflowCallOutput>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in outputsMap.Children)
        {
            var outputName = ReadMapKey(errors, keyNode, outputsPath);
            if (outputName is null)
            {
                continue;
            }

            var outputPath = $"{outputsPath}.{outputName}";
            if (valueNode is not YamlMappingNode outputMap)
            {
                errors.Add($"{outputPath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, outputMap, WorkflowCallOutputKeys, outputPath);

            var description = ReadOptionalScalar(errors, outputMap, "description", $"{outputPath}.description");
            var value = ReadRequiredScalar(errors, outputMap, "value", $"{outputPath}.value");
            if (value is not null)
            {
                outputs[outputName] = new WorkflowCallOutput(outputName, description, value);
            }
        }

        return outputs;
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
        string eventName,
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
            AddUnsupportedTriggerConfigurationKeyErrors(errors, map, eventName, path);
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
        string eventName,
        string path)
    {
        var isWorkflowCall = string.Equals(eventName, "workflow_call", StringComparison.Ordinal);
        var supportedKeys = isWorkflowCall
            ? WorkflowCallKeys
            : TriggerConfigurationKeys;

        foreach (var keyNode in map.Children.Keys)
        {
            if (keyNode is not YamlScalarNode scalar || scalar.Value is null)
            {
                continue;
            }

            if (supportedKeys.Contains(scalar.Value))
            {
                continue;
            }

            if (isWorkflowCall)
            {
                errors.Add($"{path}.{scalar.Value} is not supported in workflow_call reusable workflow definitions.");
                continue;
            }

            errors.Add($"{path}.{scalar.Value} is not supported in workflow trigger configuration.");
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

        return ReadStringMapEntries(errors, valueMap, path);
    }

    private static IReadOnlyDictionary<string, string> ReadStringMapEntries(
        List<string> errors,
        YamlMappingNode valueMap,
        string path)
    {
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

    private static WorkflowJobContainer? ReadJobContainer(
        List<string> errors,
        YamlMappingNode jobMap,
        string jobName)
    {
        var path = $"workflow.jobs.{jobName}.container";
        if (!TryGet(jobMap, "container", out var node))
        {
            return null;
        }

        if (node is YamlScalarNode scalar)
        {
            var image = ReadScalarValue(errors, scalar, path);
            return image is null ? null : new WorkflowJobContainer(image);
        }

        if (node is not YamlMappingNode containerMap)
        {
            errors.Add($"{path} must be a string or a mapping.");
            return null;
        }

        AddUnknownKeyErrors(errors, containerMap, JobContainerKeys, path);
        var containerImage = ReadRequiredScalar(errors, containerMap, "image", $"{path}.image");
        var containerEnv = ReadOptionalStringMap(errors, containerMap, "env", $"{path}.env");
        var ports = ReadContainerPorts(errors, containerMap, path);
        var volumes = ReadContainerVolumes(errors, containerMap, path);
        var options = ReadContainerOptions(errors, containerMap, path);

        return containerImage is null
            ? null
            : new WorkflowJobContainer(containerImage, containerEnv, ports, volumes, options);
    }

    private static IReadOnlyDictionary<string, WorkflowJobService> ReadJobServices(
        List<string> errors,
        YamlMappingNode jobMap,
        string jobName)
    {
        var path = $"workflow.jobs.{jobName}.services";
        if (!TryGet(jobMap, "services", out var node))
        {
            return new Dictionary<string, WorkflowJobService>();
        }

        if (node is not YamlMappingNode servicesMap)
        {
            errors.Add($"{path} must be a mapping.");
            return new Dictionary<string, WorkflowJobService>();
        }

        if (servicesMap.Children.Count == 0)
        {
            errors.Add($"{path} must contain at least one service.");
            return new Dictionary<string, WorkflowJobService>();
        }

        var services = new Dictionary<string, WorkflowJobService>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in servicesMap.Children)
        {
            var serviceName = ReadMapKey(errors, keyNode, path);
            if (serviceName is null)
            {
                continue;
            }

            var servicePath = $"{path}.{serviceName}";
            if (!IsSafeDockerAlias(serviceName))
            {
                errors.Add($"{servicePath} must use a Docker-safe service name.");
                continue;
            }

            if (valueNode is not YamlMappingNode serviceMap)
            {
                errors.Add($"{servicePath} must be a mapping.");
                continue;
            }

            AddUnknownKeyErrors(errors, serviceMap, JobServiceKeys, servicePath);
            var image = ReadRequiredScalar(errors, serviceMap, "image", $"{servicePath}.image");
            var env = ReadOptionalStringMap(errors, serviceMap, "env", $"{servicePath}.env");
            var ports = ReadContainerPorts(errors, serviceMap, servicePath);
            var volumes = ReadContainerVolumes(errors, serviceMap, servicePath);
            var options = ReadContainerOptions(errors, serviceMap, servicePath);

            if (image is not null)
            {
                services[serviceName] = new WorkflowJobService(image, env, ports, volumes, options);
            }
        }

        return services;
    }

    private static IReadOnlyList<string> ReadContainerPorts(
        List<string> errors,
        YamlMappingNode containerMap,
        string path)
    {
        var ports = ReadOptionalStringList(errors, containerMap, "ports", $"{path}.ports");
        foreach (var port in ports)
        {
            if (ContainsWhitespace(port) || port.StartsWith("-", StringComparison.Ordinal))
            {
                errors.Add($"{path}.ports contains invalid Docker port mapping '{port}'.");
            }
        }

        return ports;
    }

    private static IReadOnlyList<WorkflowJobContainerVolume> ReadContainerVolumes(
        List<string> errors,
        YamlMappingNode containerMap,
        string path)
    {
        var volumes = ReadOptionalStringList(errors, containerMap, "volumes", $"{path}.volumes");
        var parsedVolumes = new List<WorkflowJobContainerVolume>();

        for (var index = 0; index < volumes.Count; index++)
        {
            var volume = ReadContainerVolume(errors, volumes[index], $"{path}.volumes[{index}]");
            if (volume is not null)
            {
                parsedVolumes.Add(volume);
            }
        }

        return parsedVolumes;
    }

    private static WorkflowJobContainerVolume? ReadContainerVolume(
        List<string> errors,
        string value,
        string path)
    {
        var segments = value.Split(':', StringSplitOptions.TrimEntries);
        if (segments.Length is < 2 or > 3)
        {
            errors.Add($"{path} must use '<workspace-relative-source>:<absolute-container-path>[:ro|rw]'.");
            return null;
        }

        var source = segments[0];
        var target = segments[1];
        var mode = segments.Length == 3 ? segments[2] : "rw";

        if (!IsSafeRelativePath(source))
        {
            errors.Add($"{path} source must be a relative path inside the workspace.");
            return null;
        }

        if (!IsSafeContainerPath(target))
        {
            errors.Add($"{path} target must be an absolute container path outside /actio/env.");
            return null;
        }

        if (mode is not ("ro" or "rw"))
        {
            errors.Add($"{path} mode must be ro or rw.");
            return null;
        }

        return new WorkflowJobContainerVolume(source, target, string.Equals(mode, "ro", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ReadContainerOptions(
        List<string> errors,
        YamlMappingNode containerMap,
        string path)
    {
        var options = ReadOptionalScalar(errors, containerMap, "options", $"{path}.options");
        if (options is null)
        {
            return [];
        }

        var tokens = options.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsedOptions = new List<string>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Contains('='))
            {
                var separatorIndex = token.IndexOf('=', StringComparison.Ordinal);
                var optionName = token[..separatorIndex];
                var optionValue = token[(separatorIndex + 1)..];
                if (!SupportedContainerOptionsWithValues.Contains(optionName))
                {
                    errors.Add($"{path}.options contains unsupported Docker option '{optionName}'.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(optionValue))
                {
                    errors.Add($"{path}.options option '{optionName}' requires a value.");
                    continue;
                }

                parsedOptions.Add(token);
                continue;
            }

            if (SupportedContainerOptionsWithoutValues.Contains(token))
            {
                parsedOptions.Add(token);
                continue;
            }

            if (!SupportedContainerOptionsWithValues.Contains(token))
            {
                errors.Add($"{path}.options contains unsupported Docker option '{token}'.");
                continue;
            }

            if (index + 1 >= tokens.Length || tokens[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                errors.Add($"{path}.options option '{token}' requires a value.");
                continue;
            }

            parsedOptions.Add(token);
            parsedOptions.Add(tokens[++index]);
        }

        return parsedOptions;
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

    private static WorkflowJobStrategy ReadJobStrategy(
        List<string> errors,
        YamlMappingNode jobMap,
        string jobName)
    {
        if (!TryGet(jobMap, "strategy", out var node))
        {
            return WorkflowJobStrategy.Empty;
        }

        var path = $"workflow.jobs.{jobName}.strategy";
        if (node is not YamlMappingNode strategyMap)
        {
            errors.Add($"{path} must be a mapping.");
            return WorkflowJobStrategy.Empty;
        }

        AddUnknownKeyErrors(errors, strategyMap, JobStrategyKeys, path);
        var failFast = ReadOptionalBoolean(errors, strategyMap, "fail-fast", $"{path}.fail-fast") ?? true;
        var maxParallel = ReadOptionalPositiveInt(errors, strategyMap, "max-parallel", $"{path}.max-parallel");
        if (!TryGet(strategyMap, "matrix", out _))
        {
            errors.Add($"{path}.matrix is required.");
            return WorkflowJobStrategy.Empty;
        }

        return new WorkflowJobStrategy(
            ReadJobMatrix(errors, strategyMap, $"{path}.matrix"),
            failFast,
            maxParallel);
    }

    private static WorkflowJobMatrix ReadJobMatrix(
        List<string> errors,
        YamlMappingNode strategyMap,
        string path)
    {
        if (!TryGet(strategyMap, "matrix", out var node))
        {
            return WorkflowJobMatrix.Empty;
        }

        if (node is not YamlMappingNode matrixMap)
        {
            errors.Add($"{path} must be a mapping.");
            return WorkflowJobMatrix.Empty;
        }

        var axes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var include = ReadMatrixEntries(errors, matrixMap, "include", $"{path}.include");
        var exclude = ReadMatrixEntries(errors, matrixMap, "exclude", $"{path}.exclude");

        foreach (var (keyNode, valueNode) in matrixMap.Children)
        {
            var axisName = ReadMapKey(errors, keyNode, path);
            if (axisName is null)
            {
                continue;
            }

            if (axisName is "include" or "exclude")
            {
                continue;
            }

            var values = ReadMatrixAxisValues(errors, valueNode, $"{path}.{axisName}");
            if (values.Count > 0)
            {
                axes[axisName] = values;
            }
        }

        if (axes.Count == 0 && include.Count == 0)
        {
            errors.Add($"{path} must contain at least one axis or include entry.");
        }

        return new WorkflowJobMatrix(axes, include, exclude);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadMatrixEntries(
        List<string> errors,
        YamlMappingNode matrixMap,
        string key,
        string path)
    {
        if (!TryGet(matrixMap, key, out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a list of mappings.");
            return [];
        }

        var entries = new List<IReadOnlyDictionary<string, string>>();
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            if (sequence.Children[index] is not YamlMappingNode map)
            {
                errors.Add($"{itemPath} must be a mapping.");
                continue;
            }

            if (map.Children.Count == 0)
            {
                errors.Add($"{itemPath} must contain at least one value.");
                continue;
            }

            entries.Add(ReadMatrixEntry(errors, map, itemPath));
        }

        return entries;
    }

    private static IReadOnlyDictionary<string, string> ReadMatrixEntry(
        List<string> errors,
        YamlMappingNode map,
        string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
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

            var value = ReadScalarValue(errors, scalar, $"{path}.{name}");
            if (value is not null)
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static IReadOnlyList<string> ReadMatrixAxisValues(
        List<string> errors,
        YamlNode node,
        string path)
    {
        if (node is not YamlSequenceNode sequence)
        {
            errors.Add($"{path} must be a list of scalar values.");
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            errors.Add($"{path} must contain at least one value.");
            return [];
        }

        var values = new List<string>();
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlScalarNode scalar)
            {
                errors.Add($"{path}[{index}] must be a scalar value.");
                continue;
            }

            var value = ReadScalarValue(errors, scalar, $"{path}[{index}]");
            if (value is not null)
            {
                values.Add(value);
            }
        }

        return values;
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
        var workflowInputNames = CollectWorkflowInputNames(triggers);

        foreach (var job in jobs.Values)
        {
            if (job.If is null)
            {
                continue;
            }

            var expression = ValidateConditionExpression(errors, $"workflow.jobs.{job.Name}.if", job.If);
            if (expression is null)
            {
                continue;
            }

            ValidateExpressionReferences(errors, $"workflow.jobs.{job.Name}.if", expression, job, jobs, workflowInputNames, stepIndex: null);
        }

        ValidateStepConditions(errors, jobs, workflowInputNames);
    }

    private static IReadOnlySet<string> CollectWorkflowInputNames(IReadOnlyList<WorkflowTrigger> triggers)
    {
        var inputNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var trigger in triggers)
        {
            if (string.Equals(trigger.EventName, "workflow_dispatch", StringComparison.Ordinal))
            {
                foreach (var inputName in trigger.Dispatch.Inputs.Keys)
                {
                    inputNames.Add(inputName);
                }
            }

            if (string.Equals(trigger.EventName, "workflow_call", StringComparison.Ordinal))
            {
                foreach (var inputName in trigger.Call.Inputs.Keys)
                {
                    inputNames.Add(inputName);
                }
            }
        }

        return inputNames;
    }

    private static void ValidateStepConditions(
        List<string> errors,
        IReadOnlyDictionary<string, WorkflowJob> jobs,
        IReadOnlySet<string> workflowInputNames)
    {
        foreach (var job in jobs.Values)
        {
            for (var index = 0; index < job.Steps.Count; index++)
            {
                var step = job.Steps[index];
                if (step.If is null)
                {
                    continue;
                }

                var path = $"workflow.jobs.{job.Name}.steps[{index}].if";
                var expression = ValidateConditionExpression(errors, path, step.If);
                if (expression is null)
                {
                    continue;
                }

                ValidateExpressionReferences(errors, path, expression, job, jobs, workflowInputNames, index);
            }
        }
    }

    private static ExpressionNode? ValidateConditionExpression(
        List<string> errors,
        string path,
        string expression)
    {
        var parseResult = ExpressionParser.ParseTemplateExpression(expression);
        if (!parseResult.Success)
        {
            errors.Add($"{path} uses an unsupported expression: {string.Join(" ", parseResult.Errors)}");
            return null;
        }

        foreach (var function in ExpressionAnalysis.CollectFunctionCalls(parseResult.Expression!))
        {
            if (ExpressionBuiltIns.IsSupportedFunction(function.Name))
            {
                continue;
            }

            errors.Add($"{path} uses an unsupported expression: function '{function.Name}' is not supported.");
            return null;
        }

        return parseResult.Expression;
    }

    private static void ValidateExpressionReferences(
        List<string> errors,
        string path,
        ExpressionNode expression,
        WorkflowJob job,
        IReadOnlyDictionary<string, WorkflowJob> jobs,
        IReadOnlySet<string> workflowInputNames,
        int? stepIndex)
    {
        var seenReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in ExpressionAnalysis.CollectReferences(expression))
        {
            if (!seenReferences.Add(reference.ToString()))
            {
                continue;
            }

            if (string.Equals(reference.Root, "inputs", StringComparison.Ordinal))
            {
                if (reference.Path.Count != 1)
                {
                    errors.Add($"{path} references unsupported expression context '{reference}'.");
                    continue;
                }

                var inputName = reference.Path[0];
                if (!workflowInputNames.Contains(inputName))
                {
                    errors.Add($"{path} references inputs.{inputName}, but no workflow_dispatch or workflow_call input named '{inputName}' is declared.");
                }

                continue;
            }

            if (string.Equals(reference.Root, "secrets", StringComparison.Ordinal))
            {
                ValidateSingleSegmentReference(errors, path, reference);
                continue;
            }

            if (string.Equals(reference.Root, "vars", StringComparison.Ordinal))
            {
                ValidateSingleSegmentReference(errors, path, reference);
                continue;
            }

            if (string.Equals(reference.Root, "github", StringComparison.Ordinal))
            {
                ValidateGitHubReference(errors, path, reference);
                continue;
            }

            if (string.Equals(reference.Root, "needs", StringComparison.Ordinal))
            {
                ValidateNeedsReference(errors, path, reference, job, jobs);
                continue;
            }

            if (string.Equals(reference.Root, "matrix", StringComparison.Ordinal))
            {
                ValidateMatrixReference(errors, path, reference, job);
                continue;
            }

            if (string.Equals(reference.Root, "env", StringComparison.Ordinal))
            {
                ValidateSingleSegmentReference(errors, path, reference);
                continue;
            }

            if (string.Equals(reference.Root, "job", StringComparison.Ordinal))
            {
                ValidateJobReference(errors, path, reference, stepIndex);
                continue;
            }

            if (string.Equals(reference.Root, "runner", StringComparison.Ordinal))
            {
                ValidateKnownSingleSegmentReference(
                    errors,
                    path,
                    reference,
                    "name",
                    "os",
                    "environment",
                    "arch");
                continue;
            }

            if (string.Equals(reference.Root, "steps", StringComparison.Ordinal))
            {
                ValidateStepsReference(errors, path, reference, job, stepIndex);
                continue;
            }

            if (string.Equals(reference.Root, "step", StringComparison.Ordinal))
            {
                ValidateStepReference(errors, path, reference, stepIndex);
                continue;
            }

            if (IsUnavailableContext(reference.Root))
            {
                errors.Add($"{path} references expression context '{reference.Root}', which is not available in local Actio runs yet.");
                continue;
            }

            errors.Add($"{path} references unsupported expression context '{reference}'.");
        }
    }

    private static void ValidateGitHubReference(
        List<string> errors,
        string path,
        ExpressionReference reference)
    {
        if (reference.Path.Count == 1 &&
            reference.Path[0] is "event_name" or "workflow" or "workspace" or "run_id" or "job" or "actor" or "triggering_actor")
        {
            return;
        }

        if (reference.Path.Count >= 2 && string.Equals(reference.Path[0], "event", StringComparison.Ordinal))
        {
            return;
        }

        errors.Add($"{path} references unsupported expression context '{reference}'.");
    }

    private static void ValidateSingleSegmentReference(
        List<string> errors,
        string path,
        ExpressionReference reference)
    {
        if (reference.Path.Count == 1)
        {
            return;
        }

        errors.Add($"{path} references unsupported expression context '{reference}'.");
    }

    private static void ValidateKnownSingleSegmentReference(
        List<string> errors,
        string path,
        ExpressionReference reference,
        params string[] allowedNames)
    {
        if (reference.Path.Count == 1 && allowedNames.Contains(reference.Path[0]))
        {
            return;
        }

        errors.Add($"{path} references unsupported expression context '{reference}'.");
    }

    private static void ValidateJobReference(
        List<string> errors,
        string path,
        ExpressionReference reference,
        int? stepIndex)
    {
        if (stepIndex is null)
        {
            errors.Add($"{path} references expression context 'job', which is available only in step conditions.");
            return;
        }

        ValidateKnownSingleSegmentReference(
            errors,
            path,
            reference,
            "id",
            "name",
            "status",
            "runs-on",
            "runs_on");
    }

    private static void ValidateStepsReference(
        List<string> errors,
        string path,
        ExpressionReference reference,
        WorkflowJob job,
        int? stepIndex)
    {
        if (stepIndex is null)
        {
            errors.Add($"{path} references expression context 'steps', which is available only in step conditions.");
            return;
        }

        if (reference.Path.Count is not (2 or 3))
        {
            errors.Add($"{path} references unsupported expression context '{reference}'.");
            return;
        }

        var stepId = reference.Path[0];
        var referencedStepIndex = job.Steps
            .Select((step, index) => new { step.Id, Index = index })
            .FirstOrDefault(step => string.Equals(step.Id, stepId, StringComparison.Ordinal))?
            .Index;

        if (referencedStepIndex is null)
        {
            errors.Add($"{path} references unknown step id '{stepId}'.");
            return;
        }

        if (referencedStepIndex >= stepIndex)
        {
            errors.Add($"{path} references steps.{stepId}, but only previous steps are available in step conditions.");
            return;
        }

        if (reference.Path.Count == 2 && reference.Path[1] is "outcome" or "conclusion")
        {
            return;
        }

        if (reference.Path.Count == 3 && string.Equals(reference.Path[1], "outputs", StringComparison.Ordinal))
        {
            return;
        }

        errors.Add($"{path} references unsupported expression context '{reference}'.");
    }

    private static void ValidateStepReference(
        List<string> errors,
        string path,
        ExpressionReference reference,
        int? stepIndex)
    {
        if (stepIndex is null)
        {
            errors.Add($"{path} references expression context 'step', which is available only in step conditions.");
            return;
        }

        ValidateKnownSingleSegmentReference(errors, path, reference, "id", "name");
    }

    private static bool IsUnavailableContext(string root)
    {
        return root is "strategy";
    }

    private static void ValidateMatrixReference(
        List<string> errors,
        string path,
        ExpressionReference reference,
        WorkflowJob job)
    {
        if (reference.Path.Count != 1)
        {
            errors.Add($"{path} references unsupported expression context '{reference}'.");
            return;
        }

        var axisName = reference.Path[0];
        if (job.Strategy.Matrix.Axes.ContainsKey(axisName) ||
            job.Strategy.Matrix.Include.Any(entry => entry.ContainsKey(axisName)))
        {
            return;
        }

        if (job.Strategy.Matrix.Axes.Count == 0)
        {
            errors.Add($"{path} references matrix.{axisName}, but workflow.jobs.{job.Name}.strategy.matrix is not declared.");
            return;
        }

        errors.Add($"{path} references matrix.{axisName}, but workflow.jobs.{job.Name}.strategy.matrix.{axisName} is not declared.");
    }

    private static void ValidateNeedsReference(
        List<string> errors,
        string path,
        ExpressionReference reference,
        WorkflowJob job,
        IReadOnlyDictionary<string, WorkflowJob> jobs)
    {
        if (reference.Path.Count == 2 && string.Equals(reference.Path[1], "result", StringComparison.Ordinal))
        {
            ValidateDeclaredNeed(errors, path, reference, job, jobs);
            return;
        }

        if (reference.Path.Count != 3 || !string.Equals(reference.Path[1], "outputs", StringComparison.Ordinal))
        {
            errors.Add($"{path} references unsupported expression context '{reference}'.");
            return;
        }

        ValidateDeclaredNeed(errors, path, reference, job, jobs);
    }

    private static void ValidateDeclaredNeed(
        List<string> errors,
        string path,
        ExpressionReference reference,
        WorkflowJob job,
        IReadOnlyDictionary<string, WorkflowJob> jobs)
    {
        var referencedJob = reference.Path[0];
        if (!jobs.ContainsKey(referencedJob))
        {
            errors.Add($"{path} references unknown job '{referencedJob}'.");
            return;
        }

        if (!job.Needs.Contains(referencedJob, StringComparer.Ordinal))
        {
            errors.Add($"{path} references needs.{referencedJob}, but '{referencedJob}' is not declared in workflow.jobs.{job.Name}.needs.");
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

    private static bool IsSafeContainerPath(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal) || ContainsWhitespace(path))
        {
            return false;
        }

        var normalized = path.TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        if (string.Equals(normalized, "/actio/env", StringComparison.Ordinal) ||
            normalized.StartsWith("/actio/env/", StringComparison.Ordinal))
        {
            return false;
        }

        return !normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }

    private static bool IsSafeDockerAlias(string value)
    {
        return value.Length <= 63 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.');
    }

    private static bool ContainsWhitespace(string value)
    {
        return value.Any(char.IsWhiteSpace);
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

    private static void ValidateCheckoutShimInputs(
        List<string> errors,
        string itemPath,
        string? uses,
        IReadOnlyDictionary<string, string> with)
    {
        if (uses is null || with.Count == 0)
        {
            return;
        }

        if (!ActionReference.TryParse(uses, out var reference) ||
            !reference!.TryGetGitHubAction(out var action))
        {
            return;
        }

        if (string.Equals(action!.Owner, "actions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Repository, "checkout", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(action.ActionPath) &&
            string.Equals(action.Ref, "v4", StringComparison.Ordinal))
        {
            errors.Add($"{itemPath}.with is not supported by the actions/checkout@v4 Actio shim yet.");
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
