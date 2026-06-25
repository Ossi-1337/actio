using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Actio.Storage;

public sealed class GitHubActionClient : IGitHubActionClient
{
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _httpClient;

    public GitHubActionClient()
        : this(SharedClient)
    {
    }

    public GitHubActionClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> ResolveCommitShaAsync(
        string owner,
        string repository,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var url = $"repos/{owner}/{repository}/commits/{Uri.EscapeDataString(reference)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new GitHubActionClientException(
                $"GitHub repository '{owner}/{repository}' or ref '{reference}' could not be resolved. The repository must be public and the ref must exist.");
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
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new GitHubActionClientException(
                $"GitHub archive for '{owner}/{repository}@{commitSha}' could not be downloaded. The repository must be public.");
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
}
