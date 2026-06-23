namespace Actio.Engine.Execution;

public sealed record StepExecutionResult
{
    public StepExecutionResult(
        int exitCode,
        IReadOnlyList<string>? outputLines = null,
        IReadOnlyList<string>? errorLines = null)
    {
        ExitCode = exitCode;
        OutputLines = outputLines ?? [];
        ErrorLines = errorLines ?? [];
    }

    public int ExitCode { get; init; }

    public IReadOnlyList<string> OutputLines { get; init; }

    public IReadOnlyList<string> ErrorLines { get; init; }

    public bool Success => ExitCode == 0;
}
