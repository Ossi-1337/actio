using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public interface IRunnerProvider
{
    RunnerSecurityMetadata SecurityMetadata { get; }

    bool SupportsRunner(string runsOn);

    Task<JobRuntimeStartResult> StartJobRuntimeAsync(
        JobRuntimeStartRequest request,
        CancellationToken cancellationToken = default);

    Task<JobRuntimeStopResult> StopJobRuntimeAsync(
        JobRuntimeContext runtime,
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
