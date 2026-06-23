using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal sealed record JobExecutionPlan(
    IReadOnlyList<WorkflowJob> Jobs,
    IReadOnlyList<string> Errors);
