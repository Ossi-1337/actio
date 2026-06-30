namespace Actio.Engine.Execution;

public interface IRunnerProvider
{
    bool SupportsRunner(string runsOn);

    Task<ServiceContainerStartResult> StartServiceContainersAsync(
        ServiceContainerStartRequest request,
        CancellationToken cancellationToken = default);

    Task<ServiceContainerStopResult> StopServiceContainersAsync(
        JobServiceNetwork network,
        CancellationToken cancellationToken = default);

    Task<StepExecutionResult> ExecuteStepAsync(
        StepExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default);

    Task<StepExecutionResult> ExecuteDockerActionAsync(
        DockerActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default);

    Task<StepExecutionResult> ExecuteDockerfileActionAsync(
        DockerfileActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default);

    Task<StepExecutionResult> ExecuteJavaScriptActionAsync(
        JavaScriptActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default);
}
