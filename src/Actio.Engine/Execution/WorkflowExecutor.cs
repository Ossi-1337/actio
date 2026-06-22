using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

public sealed class WorkflowExecutor : IWorkflowExecutor
{
    private readonly IRunnerProvider _runnerProvider;

    public WorkflowExecutor(IRunnerProvider runnerProvider)
    {
        _runnerProvider = runnerProvider;
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDocument workflow,
        WorkflowExecutionOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateMilestoneScope(workflow);
        var totalSteps = workflow.StepCount;

        if (errors.Count > 0)
        {
            return new WorkflowExecutionResult(WorkflowExecutionStatus.Failed, 0, totalSteps, errors);
        }

        var successfulSteps = 0;

        foreach (var job in workflow.Jobs.Values)
        {
            if (!_runnerProvider.SupportsRunner(job.RunsOn))
            {
                return Failed(
                    successfulSteps,
                    totalSteps,
                    $"workflow.jobs.{job.Name}.runs-on '{job.RunsOn}' is not supported by the configured runner provider.");
            }

            foreach (var step in job.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.WriteLine($"[{job.Name}] {step.Name}");

                var result = await _runnerProvider.ExecuteStepAsync(
                    new StepExecutionRequest(
                        job.Name,
                        step.Name,
                        job.RunsOn,
                        step.Run!,
                        options.ProjectRoot,
                        workflow.Env),
                    output,
                    error,
                    cancellationToken);

                if (!result.Success)
                {
                    return Failed(
                        successfulSteps,
                        totalSteps,
                        $"workflow.jobs.{job.Name}.steps.{step.Name} failed with exit code {result.ExitCode}.");
                }

                successfulSteps++;
            }
        }

        return new WorkflowExecutionResult(WorkflowExecutionStatus.Success, successfulSteps, totalSteps, []);
    }

    private static List<string> ValidateMilestoneScope(WorkflowDocument workflow)
    {
        var errors = new List<string>();

        foreach (var job in workflow.Jobs.Values)
        {
            if (job.Needs.Count > 0)
            {
                errors.Add($"workflow.jobs.{job.Name}.needs execution is reserved for the job DAG milestone.");
            }

            if (job.If is not null)
            {
                errors.Add($"workflow.jobs.{job.Name}.if execution is reserved for the job DAG milestone.");
            }

            if (job.Outputs.Count > 0)
            {
                errors.Add($"workflow.jobs.{job.Name}.outputs execution is reserved for the history/artifacts milestone.");
            }
        }

        return errors;
    }

    private static WorkflowExecutionResult Failed(int successfulSteps, int totalSteps, string error)
    {
        return new WorkflowExecutionResult(WorkflowExecutionStatus.Failed, successfulSteps, totalSteps, [error]);
    }
}
