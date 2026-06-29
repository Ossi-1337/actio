using System.Runtime.InteropServices;
using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal static class DefaultEnvironmentVariables
{
    public const string RunnerEnvironment = "docker";
    public const string RunnerOs = "Linux";
    public const string Workspace = "/workspace";

    public static IReadOnlyDictionary<string, string> Create(
        string workflowName,
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        string runId,
        WorkflowRunTrigger runTrigger)
    {
        var actor = GetLocalActor();
        var stepIdentity = step.Id ?? $"step_{stepIndex + 1}";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ACTIO"] = "true",
            ["ACTIO_EVENT_NAME"] = runTrigger.EventName,
            ["ACTIO_EVENT_SOURCE"] = runTrigger.Source,
            ["ACTIO_JOB"] = job.Name,
            ["ACTIO_RUN_ID"] = runId,
            ["ACTIO_STEP"] = stepIdentity,
            ["ACTIO_STEP_NAME"] = step.Name,
            ["ACTIO_WORKFLOW"] = workflowName,
            ["ACTIO_WORKSPACE"] = Workspace,
            ["CI"] = "true",
            ["GITHUB_ACTION"] = stepIdentity,
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_ACTOR"] = actor,
            ["GITHUB_EVENT_NAME"] = runTrigger.EventName,
            ["GITHUB_JOB"] = job.Name,
            ["GITHUB_RUN_ATTEMPT"] = "1",
            ["GITHUB_RUN_ID"] = runId,
            ["GITHUB_TRIGGERING_ACTOR"] = actor,
            ["GITHUB_WORKFLOW"] = workflowName,
            ["GITHUB_WORKSPACE"] = Workspace,
            ["RUNNER_ARCH"] = CreateRunnerArchitecture(),
            ["RUNNER_ENVIRONMENT"] = RunnerEnvironment,
            ["RUNNER_NAME"] = job.RunsOn,
            ["RUNNER_OS"] = RunnerOs
        };

        foreach (var item in job.Matrix)
        {
            environment[$"ACTIO_MATRIX_{ToEnvironmentSegment(item.Key)}"] = item.Value;
        }

        return environment;
    }

    public static string CreateRunnerArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    public static string GetLocalActor()
    {
        return string.IsNullOrWhiteSpace(Environment.UserName)
            ? "local"
            : Environment.UserName;
    }

    private static string ToEnvironmentSegment(string value)
    {
        var characters = value
            .Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray();
        var segment = new string(characters);
        return string.IsNullOrEmpty(segment) ? "VALUE" : segment;
    }
}
