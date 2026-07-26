namespace Actio.Storage;

internal sealed class CacheEntryLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private CacheEntryLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<CacheEntryLock> AcquireAsync(
        string actioHome,
        string category,
        string key,
        CancellationToken cancellationToken)
    {
        var lockDirectory = Path.Combine(
            Path.GetFullPath(actioHome),
            "cache",
            ".locks",
            category);
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{key}.lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new CacheEntryLock(File.Open(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None));
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
