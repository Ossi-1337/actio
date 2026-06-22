namespace Actio.Engine.Execution;

public sealed record StepExecutionResult(
    int ExitCode)
{
    public bool Success => ExitCode == 0;
}
