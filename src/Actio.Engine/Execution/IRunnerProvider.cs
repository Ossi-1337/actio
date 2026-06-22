namespace Actio.Engine.Execution;

public interface IRunnerProvider
{
    bool SupportsRunner(string runsOn);

    Task<StepExecutionResult> ExecuteStepAsync(
        StepExecutionRequest request,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default);
}
