using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Actio.Storage;

public sealed class GitHubActionClient : IGitHubActionClient
{
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _httpClient;
    private readonly IGitHubTokenProvider _tokenProvider;

    public GitHubActionClient()
        : this(SharedClient)
    {
    }

    public GitHubActionClient(
        HttpClient httpClient,
        IGitHubTokenProvider? tokenProvider = null)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider ?? new EnvironmentGitHubTokenProvider();
    }

    public async Task<string> ResolveCommitShaAsync(
        string owner,
        string repository,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var url = $"repos/{owner}/{repository}/commits/{Uri.EscapeDataString(reference)}";
        var token = _tokenProvider.GetToken();
        using var request = CreateRequest(HttpMethod.Get, url, token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new GitHubActionClientException(FormatResolveNotFoundMessage(owner, repository, reference, token));
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new GitHubActionClientException(FormatResolveAccessMessage(owner, repository, reference, token, response));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubActionClientException(
                $"GitHub ref '{owner}/{repository}@{reference}' could not be resolved. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("sha", out var shaElement) ||
            shaElement.GetString() is not { Length: 40 } sha)
        {
            throw new GitHubActionClientException(
                $"GitHub ref '{owner}/{repository}@{reference}' did not return a commit SHA.");
        }

        return sha;
    }

    public async Task DownloadArchiveAsync(
        string owner,
        string repository,
        string commitSha,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var url = $"repos/{owner}/{repository}/zipball/{commitSha}";
        var token = _tokenProvider.GetToken();
        using var request = CreateRequest(HttpMethod.Get, url, token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new GitHubActionClientException(FormatArchiveNotFoundMessage(owner, repository, commitSha, token));
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new GitHubActionClientException(FormatArchiveAccessMessage(owner, repository, commitSha, token, response));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubActionClientException(
                $"GitHub archive for '{owner}/{repository}@{commitSha}' could not be downloaded. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        GitHubToken? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            if (string.IsNullOrWhiteSpace(token.Value) ||
                token.Value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                throw new GitHubActionClientException($"{token.SourceName} contains invalid characters.");
            }

            try
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            }
            catch (FormatException ex)
            {
                throw new GitHubActionClientException($"{token.SourceName} contains invalid characters.", ex);
            }
        }

        return request;
    }

    private static string FormatResolveNotFoundMessage(
        string owner,
        string repository,
        string reference,
        GitHubToken? token)
    {
        return token is null
            ? $"GitHub repository '{owner}/{repository}' or ref '{reference}' could not be resolved. If this is a private repository, set {EnvironmentGitHubTokenProvider.TokenEnvironmentVariable} with read access."
            : $"GitHub repository '{owner}/{repository}' or ref '{reference}' could not be resolved. Check that the repository/ref exists and {token.SourceName} has read access.";
    }

    private static string FormatResolveAccessMessage(
        string owner,
        string repository,
        string reference,
        GitHubToken? token,
        HttpResponseMessage response)
    {
        return token is null
            ? $"GitHub ref '{owner}/{repository}@{reference}' could not be accessed. If this is a private repository, set {EnvironmentGitHubTokenProvider.TokenEnvironmentVariable} with read access. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}."
            : $"GitHub ref '{owner}/{repository}@{reference}' could not be accessed. Check that {token.SourceName} has read access. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static string FormatArchiveNotFoundMessage(
        string owner,
        string repository,
        string commitSha,
        GitHubToken? token)
    {
        return token is null
            ? $"GitHub archive for '{owner}/{repository}@{commitSha}' could not be downloaded. If this is a private repository, set {EnvironmentGitHubTokenProvider.TokenEnvironmentVariable} with read access."
            : $"GitHub archive for '{owner}/{repository}@{commitSha}' could not be downloaded. Check that {token.SourceName} has read access.";
    }

    private static string FormatArchiveAccessMessage(
        string owner,
        string repository,
        string commitSha,
        GitHubToken? token,
        HttpResponseMessage response)
    {
        return token is null
            ? $"GitHub archive for '{owner}/{repository}@{commitSha}' could not be accessed. If this is a private repository, set {EnvironmentGitHubTokenProvider.TokenEnvironmentVariable} with read access. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}."
            : $"GitHub archive for '{owner}/{repository}@{commitSha}' could not be accessed. Check that {token.SourceName} has read access. GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Actio", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

public sealed class GitHubActionClientException : Exception
{
    public GitHubActionClientException(string message)
        : base(message)
    {
    }

    public GitHubActionClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
