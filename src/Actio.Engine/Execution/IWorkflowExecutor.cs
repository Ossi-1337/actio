using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

public interface IWorkflowExecutor
{
    Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDocument workflow,
        WorkflowExecutionOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default);
}
