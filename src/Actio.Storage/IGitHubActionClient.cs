namespace Actio.Storage;

public interface IGitHubActionClient
{
    Task<string> ResolveCommitShaAsync(
        string owner,
        string repository,
        string reference,
        CancellationToken cancellationToken = default);

    Task DownloadArchiveAsync(
        string owner,
        string repository,
        string commitSha,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
