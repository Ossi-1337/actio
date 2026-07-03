using Actio.Core.Actions;
using Actio.Core.Workflows;

namespace Actio.Core.Security;

public static class WorkflowSecurityPolicy
{
    public static IReadOnlyList<WorkflowSecurityFinding> Analyze(WorkflowDocument workflow)
    {
        var findings = new List<WorkflowSecurityFinding>();

        AddTriggerFindings(findings, workflow);
        AddActionFindings(findings, workflow);

        return findings;
    }

    private static void AddTriggerFindings(List<WorkflowSecurityFinding> findings, WorkflowDocument workflow)
    {
        foreach (var trigger in workflow.Triggers)
        {
            if (!string.Equals(trigger.EventName, "pull_request_target", StringComparison.Ordinal))
            {
                continue;
            }

            findings.Add(new WorkflowSecurityFinding(
                "warning",
                "unsafe-trigger",
                "workflow.on.pull_request_target",
                "pull_request_target is security-sensitive because GitHub runs it with base-repository context.",
                "Keep it local metadata unless a future hosted trust model explicitly handles fork trust, tokens, and permission elevation."));
        }
    }

    private static void AddActionFindings(List<WorkflowSecurityFinding> findings, WorkflowDocument workflow)
    {
        foreach (var job in workflow.Jobs.Values)
        {
            for (var index = 0; index < job.Steps.Count; index++)
            {
                var uses = job.Steps[index].Uses;
                if (uses is null || !ActionReference.TryParse(uses, out var reference) || reference is null || !reference.IsRemote)
                {
                    continue;
                }

                findings.Add(CreateExternalActionFinding(
                    reference,
                    $"workflow.jobs.{job.Name}.steps[{index}].uses"));
            }
        }
    }

    private static WorkflowSecurityFinding CreateExternalActionFinding(
        ActionReference reference,
        string location)
    {
        if (reference.IsMutable)
        {
            return new WorkflowSecurityFinding(
                "warning",
                "external-action.mutable-ref",
                location,
                $"External action '{reference.Value}' uses mutable identity '{reference.MutablePart}'.",
                GetMutableReferenceRecommendation(reference),
                reference.Value,
                reference.Kind.ToString(),
                false,
                reference.MutablePart);
        }

        return new WorkflowSecurityFinding(
            "info",
            "external-action.pinned-ref",
            location,
            $"External action '{reference.Value}' is pinned.",
            "Pinned external action identity is recorded for audit. Keep reviewing source trust before reuse.",
            reference.Value,
            reference.Kind.ToString(),
            true,
            null);
    }

    private static string GetMutableReferenceRecommendation(ActionReference reference)
    {
        return reference.Kind == ActionReferenceKind.DockerImage
            ? "Pin Docker image actions with a sha256 digest for safer reuse."
            : "Pin GitHub actions with a full commit SHA for safer reuse.";
    }
}
