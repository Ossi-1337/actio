using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public static class WorkflowRerunOptionsFactory
{
    public static WorkflowExecutionOptions Create(
        WorkflowRunRecord sourceRun,
        string runId,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> secrets,
        IReadOnlyDictionary<string, string> variables,
        ContainerResourceConfiguration resourceConfiguration,
        ActioInstanceIdentity instanceIdentity)
    {
        var profile = sourceRun.RunnerSecurity?.RequestedProfile ??
            RunnerSecurityProfiles.SecureBaseline;
        return new WorkflowExecutionOptions(
            sourceRun.ProjectRoot,
            sourceRun.WorkflowPath,
            runId,
            new WorkflowRunTrigger(
                "workflow_dispatch",
                $"rerun:{sourceRun.RunId}",
                inputs),
            Secrets: secrets,
            Variables: variables,
            RunnerPolicy: new RunnerExecutionPolicy(
                profile,
                resourceConfiguration,
                instanceIdentity));
    }
}
