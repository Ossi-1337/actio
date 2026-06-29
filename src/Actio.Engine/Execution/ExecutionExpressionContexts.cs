using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Actio.Core.Expressions;
using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal static class ExecutionExpressionContexts
{
    public static ExpressionContextData ForJob(
        WorkflowDocument workflow,
        WorkflowJob job,
        WorkflowExecutionOptions options,
        string runId,
        IReadOnlyDictionary<string, string> env,
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
            options.RunTrigger.Inputs,
            jobOutputs,
            jobStatuses,
            job.Needs,
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
            runTrigger.Inputs,
            jobOutputs,
            jobStatuses,
            job.Needs,
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

    private static ExpressionContextData Create(
        string workflowName,
        WorkflowJob job,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> env,
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
            ExpressionContextRoot.AvailableRoot("github", CreateGitHubContext(workflowName, projectRoot, runId, runTrigger, job.Name), includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("env", ExpressionContextData.FromStrings(env), allowMissingProperties: true, includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("job", CreateJobContext(job, jobStatus), includeInSafeSnapshot: true),
            ExpressionContextRoot.AvailableRoot("runner", CreateRunnerContext(job.RunsOn), includeInSafeSnapshot: true),
            ExpressionContextRoot.AvailableRoot("needs", CreateNeedsContext(neededJobs, jobOutputs, jobStatuses), allowMissingProperties: true, includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("steps", CreateStepsContext(stepOutputs, stepStatuses), allowMissingProperties: true, includeInSafeSnapshot: false),
            ExpressionContextRoot.AvailableRoot("inputs", ExpressionContextData.FromStrings(inputs), allowMissingProperties: true, includeInSafeSnapshot: false)
        };

        if (step is not null)
        {
            roots.Add(ExpressionContextRoot.AvailableRoot("step", CreateStepContext(step), includeInSafeSnapshot: true));
        }

        roots.AddRange(CreateUnavailableRoots());
        return new ExpressionContextData(roots, projectRoot);
    }

    private static JsonObject CreateGitHubContext(
        string workflowName,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        string jobName)
    {
        return new JsonObject
        {
            ["event_name"] = runTrigger.EventName,
            ["workflow"] = workflowName,
            ["workspace"] = Path.GetFullPath(projectRoot),
            ["run_id"] = runId,
            ["job"] = jobName,
            ["event"] = CreateEventContext(runTrigger.EventPayload)
        };
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
            ["os"] = "Linux",
            ["environment"] = "docker",
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString()
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

    private static IEnumerable<ExpressionContextRoot> CreateUnavailableRoots()
    {
        yield return ExpressionContextRoot.UnavailableRoot("vars", "Expression context 'vars' is not available until a local vars provider is implemented.");
        yield return ExpressionContextRoot.UnavailableRoot("secrets", "Expression context 'secrets' is not available until a local secrets provider is implemented.");
        yield return ExpressionContextRoot.UnavailableRoot("strategy", "Expression context 'strategy' is not available until matrix strategy support is implemented.");
        yield return ExpressionContextRoot.UnavailableRoot("matrix", "Expression context 'matrix' is not available until matrix strategy support is implemented.");
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
