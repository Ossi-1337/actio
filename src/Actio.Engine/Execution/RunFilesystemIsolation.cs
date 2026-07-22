namespace Actio.Engine.Execution;

public sealed record RunFilesystemIsolation(
    IReadOnlyList<StepExecutionMount> WorkspaceMasks,
    string? BuildContextStagingRoot)
{
    public static RunFilesystemIsolation None { get; } = new([], null);
}
