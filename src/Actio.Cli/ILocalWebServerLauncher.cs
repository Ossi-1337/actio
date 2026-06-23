namespace Actio.Cli;

public interface ILocalWebServerLauncher
{
    Task<string?> EnsureStartedAsync(
        string projectRoot,
        string? runId,
        TextWriter error,
        CancellationToken cancellationToken = default);
}
