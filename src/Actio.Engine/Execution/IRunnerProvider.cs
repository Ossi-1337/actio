namespace Actio.Engine.Execution;

public interface IRunnerProvider
{
    bool SupportsRunner(string runsOn);

    Task<StepExecutionResult> ExecuteStepAsync(
        StepExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default);

    Task<StepExecutionResult> ExecuteDockerActionAsync(
        DockerActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default);
}
