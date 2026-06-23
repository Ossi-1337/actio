namespace Actio.Engine.Runs;

public sealed class NullStepLog : IStepLog
{
    public static NullStepLog Instance { get; } = new();

    private NullStepLog()
    {
    }

    public string? LogPath => null;

    public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
