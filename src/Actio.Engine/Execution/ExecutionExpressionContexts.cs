using System.Text.Json.Nodes;
using Actio.Core.Expressions;
using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal static class ExecutionExpressionContexts
{
    private const string MissingAutomaticGitHubTokenMessage = "Actio does not create GitHub's automatic GITHUB_TOKEN in local runs. Configure a local secret named GITHUB_TOKEN through .actio/secrets.env or ACTIO_SECRET_GITHUB_TOKEN when a workflow step or action needs a token.";

    private static readonly IReadOnlyDictionary<string, string> GitHubMissingPropertyMessages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["github.token"] = MissingAutomaticGitHubTokenMessage
    };

    private static readonly IReadOnlyDictionary<string, string> SecretsMissingPropertyMessages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["secrets.GITHUB_TOKEN"] = MissingAutomaticGitHubTokenMessage
    };

    public static ExpressionContextData ForJob(
        WorkflowDocument workflow,
        WorkflowJob job,
        WorkflowExecutionOptions options,
        string runId,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> secrets,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses)
    {
        return Create(
            workflow.Name,
            job,
            options.ProjectRoot,
            runId,
            options.RunTrigger,
            env,
            variables,
            secrets,
            options.RunTrigger.Inputs,
            jobOutputs,
            jobStatuses,
            job.LogicalNeeds,
            new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            new Dictionary<string, string>(),
            "pending");
    }

    public static ExpressionContextData ForStep(
        string workflowName,
        WorkflowJob job,
        WorkflowStep step,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> secrets,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> stepStatuses)
    {
        return Create(
            workflowName,
            job,
            projectRoot,
            runId,
            runTrigger,
            env,
            variables,
            secrets,
            runTrigger.Inputs,
            jobOutputs,
            jobStatuses,
            job.LogicalNeeds,
            stepOutputs,
            stepStatuses,
            "running",
            step);
    }

    public static ExpressionContextData ForActionInputs(
        IReadOnlyDictionary<string, string> inputs,
        string workspaceRoot)
    {
        return new ExpressionContextData(
            CreateUnavailableRoots()
                .Prepend(ExpressionContextRoot.AvailableRoot("inputs", ExpressionContextData.FromStrings(inputs), allowMissingProperties: true, includeInSafeSnapshot: false)),
            workspaceRoot);
    }

    public static ExpressionContextData ForActionOutputs(
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> stepStatuses,
        string workspaceRoot)
    {
        return new ExpressionContextData(
            CreateUnavailableRoots()
                .Prepend(ExpressionContextRoot.AvailableRoot("steps", CreateStepsContext(stepOutputs, stepStatuses), allowMissingProperties: true, includeInSafeSnapshot: false))
                .Prepend(ExpressionContextRoot.AvailableRoot("inputs", ExpressionContextData.FromStrings(inputs), allowMissingProperties: true, includeInSafeSnapshot: false)),
            workspaceRoot);
    }

    public static ExpressionContextData ForWorkflowCallValues(WorkflowExecutionOptions options)
    {
        return new ExpressionContextData(
            CreateUnavailableRoots(includeVariables: false, includeSecrets: false)
                .Prepend(ExpressionContextRoot.AvailableRoot("inputs", ExpressionContextData.FromStrings(options.RunTrigger.Inputs), allowMissingProperties: false, includeInSafeSnapshot: false))
                .Prepend(CreateSecretsRoot(options.Secrets))
                .Prepend(ExpressionContextRoot.AvailableRoot("vars", ExpressionContextData.FromStrings(options.Variables), allowMissingProperties: false, includeInSafeSnapshot: false)),
            options.ProjectRoot);
    }

    public static ExpressionContextData ForSecretBindings(IReadOnlyDictionary<string, string> secrets)
    {
        return new ExpressionContextData(
            CreateUnavailableRoots(includeSecrets: false)
                .Prepend(CreateSecretsRoot(secrets)));
    }

    private static ExpressionContextData Create(
        string workflowName,
        WorkflowJob job,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> secrets,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyList<string> neededJobs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> stepStatuses,
        string jobStatus,
        WorkflowStep? step = null)
    {
        var roots = new List<ExpressionContextRoot>
        {
            ExpressionContextRoot.AvailableRoot("github", CreateGitHubContext(workflowName, projectRoot, runId, runTrigger, job.Name), includeInSafeSnapshot: false, missingPropertyMessages: GitHubMissingPropertyMessages),
            ExpressionContextRoot.AvailableRoot("env", ExpressionContextData.FromStrings(env), allowMissingProperties: true, includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("vars", ExpressionContextData.FromStrings(variables), allowMissingProperties: false, includeInSafeSnapshot: false),
            CreateSecretsRoot(secrets),
            ExpressionContextRoot.AvailableRoot("job", CreateJobContext(job, jobStatus), includeInSafeSnapshot: true),
            ExpressionContextRoot.AvailableRoot("matrix", ExpressionContextData.FromStrings(job.Matrix), allowMissingProperties: true, includeInSafeSnapshot: true),
            ExpressionContextRoot.AvailableRoot("runner", CreateRunnerContext(job.RunsOn), includeInSafeSnapshot: true),
            ExpressionContextRoot.AvailableRoot("needs", CreateNeedsContext(neededJobs, jobOutputs, jobStatuses), allowMissingProperties: true, includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("steps", CreateStepsContext(stepOutputs, stepStatuses), allowMissingProperties: true, includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("inputs", ExpressionContextData.FromStrings(inputs), allowMissingProperties: true, includeInSafeSnapshot: false)
        };

        if (step is not null)
        {
            roots.Add(ExpressionContextRoot.AvailableRoot("step", CreateStepContext(step), includeInSafeSnapshot: true));
        }

        roots.AddRange(CreateUnavailableRoots(includeVariables: false, includeSecrets: false));
        return new ExpressionContextData(roots, projectRoot);
    }

    private static JsonObject CreateGitHubContext(
        string workflowName,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        string jobName)
    {
        var actor = DefaultEnvironmentVariables.GetLocalActor();

        return new JsonObject
        {
            ["event_name"] = runTrigger.EventName,
            ["workflow"] = workflowName,
            ["workspace"] = Path.GetFullPath(projectRoot),
            ["run_id"] = runId,
            ["job"] = jobName,
            ["actor"] = actor,
            ["triggering_actor"] = actor,
            ["event"] = CreateEventContext(runTrigger.EventPayload)
        };
    }

    private static ExpressionContextRoot CreateSecretsRoot(IReadOnlyDictionary<string, string> secrets)
    {
        return ExpressionContextRoot.AvailableRoot(
            "secrets",
            ExpressionContextData.FromStrings(secrets),
            allowMissingProperties: false,
            includeInSafeSnapshot: false,
            missingPropertyMessages: SecretsMissingPropertyMessages);
    }

    private static JsonObject CreateEventContext(WorkflowEventPayload eventPayload)
    {
        var properties = ExpressionContextData.FromStrings(eventPayload.Properties);
        properties["event_name"] = eventPayload.EventName;
        properties["eventName"] = eventPayload.EventName;
        properties["source"] = eventPayload.Source;
        properties["inputs"] = ExpressionContextData.FromStrings(eventPayload.Inputs);

        if (!string.IsNullOrWhiteSpace(eventPayload.Action))
        {
            properties["action"] = eventPayload.Action;
        }

        return properties;
    }

    private static JsonObject CreateJobContext(WorkflowJob job, string status)
    {
        return new JsonObject
        {
            ["id"] = job.Name,
            ["name"] = job.DisplayName,
            ["status"] = status,
            ["runs-on"] = job.RunsOn,
            ["runs_on"] = job.RunsOn
        };
    }

    private static JsonObject CreateStepContext(WorkflowStep step)
    {
        var context = new JsonObject
        {
            ["name"] = step.Name
        };

        if (step.Id is not null)
        {
            context["id"] = step.Id;
        }

        return context;
    }

    private static JsonObject CreateRunnerContext(string runsOn)
    {
        return new JsonObject
        {
            ["name"] = runsOn,
            ["os"] = DefaultEnvironmentVariables.RunnerOs,
            ["environment"] = DefaultEnvironmentVariables.RunnerEnvironment,
            ["arch"] = DefaultEnvironmentVariables.CreateRunnerArchitecture()
        };
    }

    private static JsonObject CreateNeedsContext(
        IReadOnlyList<string> neededJobs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses)
    {
        var needs = new JsonObject();
        foreach (var neededJob in neededJobs)
        {
            var result = jobStatuses.TryGetValue(neededJob, out var status)
                ? ToContextResult(status)
                : "skipped";

            needs[neededJob] = new JsonObject
            {
                ["result"] = result,
                ["outputs"] = jobOutputs.TryGetValue(neededJob, out var outputs)
                    ? ExpressionContextData.FromStrings(outputs)
                    : new JsonObject()
            };
        }

        return needs;
    }

    private static JsonObject CreateStepsContext(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> stepStatuses)
    {
        var steps = new JsonObject();
        foreach (var stepId in stepOutputs.Keys.Concat(stepStatuses.Keys).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            var step = new JsonObject
            {
                ["outputs"] = stepOutputs.TryGetValue(stepId, out var outputs)
                    ? ExpressionContextData.FromStrings(outputs)
                    : new JsonObject()
            };

            if (stepStatuses.TryGetValue(stepId, out var status))
            {
                var result = ToContextResult(status);
                step["outcome"] = result;
                step["conclusion"] = result;
            }

            steps[stepId] = step;
        }

        return steps;
    }

    private static IEnumerable<ExpressionContextRoot> CreateUnavailableRoots(
        bool includeVariables = true,
        bool includeSecrets = true)
    {
        if (includeVariables)
        {
            yield return ExpressionContextRoot.UnavailableRoot("vars", "Expression context 'vars' is not available until a local vars provider is implemented.");
        }

        if (includeSecrets)
        {
            yield return ExpressionContextRoot.UnavailableRoot("secrets", "Expression context 'secrets' is not available until a local secrets provider is implemented.");
        }

        yield return ExpressionContextRoot.UnavailableRoot("strategy", "Expression context 'strategy' is not available until matrix strategy metadata support is implemented.");
    }

    private static string ToContextResult(string status)
    {
        return status switch
        {
            "Success" => "success",
            "Failed" => "failure",
            "TimedOut" => "failure",
            "Skipped" => "skipped",
            "Running" => "running",
            _ => status.ToLowerInvariant()
        };
    }
}
