namespace Actio.Engine.Runs;

public interface IStepLog : IAsyncDisposable
{
    string? LogPath { get; }

    Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default);

    Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default);
}
