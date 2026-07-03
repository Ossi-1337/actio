using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed class RunCancellationWatcher : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watchTask;

    private RunCancellationWatcher(
        IRunStore runStore,
        string runId,
        CancellationTokenSource executionCancellation,
        TimeSpan pollInterval)
    {
        _watchTask = WatchAsync(runStore, runId, executionCancellation, pollInterval, _stop.Token);
    }

    public static RunCancellationWatcher Start(
        IRunStore runStore,
        string runId,
        CancellationTokenSource executionCancellation,
        TimeSpan? pollInterval = null)
    {
        return new RunCancellationWatcher(runStore, runId, executionCancellation, pollInterval ?? DefaultPollInterval);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        try
        {
            await _watchTask;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static async Task WatchAsync(
        IRunStore runStore,
        string runId,
        CancellationTokenSource executionCancellation,
        TimeSpan pollInterval,
        CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested && !executionCancellation.IsCancellationRequested)
        {
            if (await IsCancellationRequestedAsync(runStore, runId, stopToken))
            {
                await executionCancellation.CancelAsync();
                return;
            }

            await Task.Delay(pollInterval, stopToken);
        }
    }

    private static async Task<bool> IsCancellationRequestedAsync(
        IRunStore runStore,
        string runId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runStore.IsRunCancellationRequestedAsync(runId, cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return false;
        }
    }
}
