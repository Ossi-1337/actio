namespace Actio.Engine.Actions;

public interface IGitHubActionSourceProvider
{
    Task<GitHubActionSourceResult> GetGitHubActionSourceAsync(
        GitHubActionSourceRequest request,
        CancellationToken cancellationToken = default);
}
