using Actio.Core.Expressions;
using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal static class WorkflowEnvironmentResolver
{
    public static WorkflowEnvironmentResolution Resolve(
        WorkflowDocument workflow,
        IReadOnlyDictionary<string, string> secrets)
    {
        var errors = new List<string>();
        var evaluationContext = new ExpressionEvaluationContext(
            ExecutionExpressionContexts.ForSecretBindings(secrets).Resolve);
        var workflowEnv = ResolveMap(workflow.Env, "workflow.env", evaluationContext, errors);
        var jobs = new Dictionary<string, WorkflowJob>(StringComparer.Ordinal);

        foreach (var job in workflow.Jobs)
        {
            var jobPath = $"workflow.jobs.{job.Key}";
            var jobEnv = ResolveMap(job.Value.Env, $"{jobPath}.env", evaluationContext, errors);
            var container = job.Value.Container is null
                ? null
                : job.Value.Container with
                {
                    Env = ResolveMap(
                        job.Value.Container.Env,
                        $"{jobPath}.container.env",
                        evaluationContext,
                        errors)
                };
            var steps = job.Value.Steps
                .Select((step, index) => step with
                {
                    Env = ResolveMap(
                        step.Env,
                        $"{jobPath}.steps[{index}].env",
                        evaluationContext,
                        errors)
                })
                .ToArray();

            jobs[job.Key] = job.Value with
            {
                Env = jobEnv,
                Container = container,
                Steps = steps
            };
        }

        return errors.Count == 0
            ? WorkflowEnvironmentResolution.Resolved(workflow with { Env = workflowEnv, Jobs = jobs })
            : WorkflowEnvironmentResolution.Failed(errors);
    }

    private static IReadOnlyDictionary<string, string> ResolveMap(
        IReadOnlyDictionary<string, string> values,
        string path,
        ExpressionEvaluationContext evaluationContext,
        List<string> errors)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var interpolation = ExpressionTemplate.Interpolate(item.Value, evaluationContext);
            if (interpolation.Success)
            {
                resolved[item.Key] = interpolation.Value;
                continue;
            }

            foreach (var error in interpolation.Errors)
            {
                errors.Add($"{path}.{item.Key} could not be evaluated: {error}");
            }
        }

        return resolved;
    }
}

internal sealed record WorkflowEnvironmentResolution(
    bool Success,
    WorkflowDocument? Workflow,
    IReadOnlyList<string> Errors)
{
    public static WorkflowEnvironmentResolution Resolved(WorkflowDocument workflow)
        => new(true, workflow, []);

    public static WorkflowEnvironmentResolution Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}
