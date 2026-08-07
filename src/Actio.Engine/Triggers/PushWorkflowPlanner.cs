using Actio.Core.Workflows;

namespace Actio.Engine.Triggers;

public sealed record PushWorkflowSource(string WorkflowPath, WorkflowDocument Workflow);

public sealed record PushReferenceEvent(
    string FullReference,
    string ReferenceName,
    string ReferenceType,
    string BeforeSha,
    string AfterSha,
    IReadOnlyList<string> ChangedPaths)
{
    public WorkflowTriggerFilterContext ToFilterContext()
        => string.Equals(ReferenceType, "branch", StringComparison.Ordinal)
            ? new WorkflowTriggerFilterContext("push", Branch: ReferenceName, ChangedPaths: ChangedPaths)
            : new WorkflowTriggerFilterContext("push", Tag: ReferenceName, ChangedPaths: ChangedPaths);
}

public sealed record PushWorkflowPlanEntry(
    PushWorkflowSource Source,
    PushReferenceEvent Reference);

public static class PushWorkflowPlanner
{
    public static IReadOnlyList<PushWorkflowPlanEntry> Create(
        IReadOnlyList<PushWorkflowSource> workflows,
        IReadOnlyList<PushReferenceEvent> references)
    {
        var plan = new List<PushWorkflowPlanEntry>();

        foreach (var reference in references)
        {
            var context = reference.ToFilterContext();
            foreach (var workflow in workflows)
            {
                var matches = workflow.Workflow.Triggers
                    .Where(trigger => string.Equals(trigger.EventName, "push", StringComparison.Ordinal))
                    .Any(trigger => WorkflowTriggerFilterEvaluator.Evaluate(trigger, context).Matches);
                if (matches)
                {
                    plan.Add(new PushWorkflowPlanEntry(workflow, reference));
                }
            }
        }

        return plan;
    }
}
