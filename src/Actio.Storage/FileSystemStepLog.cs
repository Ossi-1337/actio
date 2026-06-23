using Actio.Engine.Runs;

namespace Actio.Storage;

internal sealed class FileSystemStepLog : IStepLog
{
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileSystemStepLog(string logPath, TextWriter writer)
    {
        LogPath = logPath;
        _writer = writer;
    }

    public string? LogPath { get; }

    public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        return WriteLineAsync($"[stdout] {line}", cancellationToken);
    }

    public Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default)
    {
        return WriteLineAsync($"[stderr] {line}", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _writeLock.Dispose();
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
