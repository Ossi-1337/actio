namespace Actio.Storage;

public interface IGitHubTokenProvider
{
    GitHubToken? GetToken();
}

public sealed record GitHubToken(
    string Value,
    string SourceName);

public sealed class EnvironmentGitHubTokenProvider : IGitHubTokenProvider
{
    public const string TokenEnvironmentVariable = "ACTIO_GITHUB_TOKEN";

    public GitHubToken? GetToken()
    {
        var value = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim();
        if (token.Any(char.IsControl))
        {
            throw new GitHubActionClientException($"{TokenEnvironmentVariable} contains invalid control characters.");
        }

        return new GitHubToken(token, TokenEnvironmentVariable);
    }
}
