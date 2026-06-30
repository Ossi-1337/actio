using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class GitHubActionClientTests
{
    [Fact]
    public async Task ResolveCommitShaAsync_SendsActioTokenAsBearerHeader()
    {
        var sha = new string('a', 40);
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse($$"""{"sha":"{{sha}}"}"""));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubActionClient(httpClient, new StaticGitHubTokenProvider("secret-token"));

        var result = await client.ResolveCommitShaAsync("owner", "repo", "main");

        Assert.Equal(sha, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("secret-token", request.Authorization?.Parameter);
    }

    [Fact]
    public async Task ResolveCommitShaAsync_OmitsAuthorizationHeaderWhenTokenIsMissing()
    {
        var sha = new string('b', 40);
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse($$"""{"sha":"{{sha}}"}"""));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubActionClient(httpClient, new StaticGitHubTokenProvider(null));

        var result = await client.ResolveCommitShaAsync("owner", "repo", "main");

        Assert.Equal(sha, result);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task ResolveCommitShaAsync_SuggestsTokenForNotFoundWithoutLeakingSecrets()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubActionClient(httpClient, new StaticGitHubTokenProvider(null));

        var exception = await Assert.ThrowsAsync<GitHubActionClientException>(
            () => client.ResolveCommitShaAsync("owner", "repo", "main"));

        Assert.Contains(EnvironmentGitHubTokenProvider.TokenEnvironmentVariable, exception.Message);
        Assert.Contains("private repository", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveCommitShaAsync_DoesNotLeakTokenWhenAccessFails()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "Forbidden"
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubActionClient(httpClient, new StaticGitHubTokenProvider("secret-token"));

        var exception = await Assert.ThrowsAsync<GitHubActionClientException>(
            () => client.ResolveCommitShaAsync("owner", "repo", "main"));

        Assert.Contains(EnvironmentGitHubTokenProvider.TokenEnvironmentVariable, exception.Message);
        Assert.Contains("read access", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveCommitShaAsync_DoesNotLeakTokenWhenTokenFormatIsInvalid()
    {
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse("""{"sha":"not-used"}"""));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubActionClient(httpClient, new StaticGitHubTokenProvider("secret token"));

        var exception = await Assert.ThrowsAsync<GitHubActionClientException>(
            () => client.ResolveCommitShaAsync("owner", "repo", "main"));

        Assert.Contains(EnvironmentGitHubTokenProvider.TokenEnvironmentVariable, exception.Message);
        Assert.DoesNotContain("secret token", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadArchiveAsync_SendsActioTokenAsBearerHeader()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("zip-content", Encoding.UTF8, "application/zip")
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubActionClient(httpClient, new StaticGitHubTokenProvider("secret-token"));
        var destinationPath = Path.Combine(Path.GetTempPath(), $"actio-github-client-{Guid.NewGuid():N}.zip");

        try
        {
            await client.DownloadArchiveAsync("owner", "repo", new string('c', 40), destinationPath);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal("secret-token", request.Authorization?.Parameter);
            Assert.Equal("zip-content", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StaticGitHubTokenProvider : IGitHubTokenProvider
    {
        private readonly string? _token;

        public StaticGitHubTokenProvider(string? token)
        {
            _token = token;
        }

        public GitHubToken? GetToken()
        {
            return _token is null
                ? null
                : new GitHubToken(_token, EnvironmentGitHubTokenProvider.TokenEnvironmentVariable);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            _handle = handle;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.RequestUri, request.Headers.Authorization));
            return Task.FromResult(_handle(request));
        }
    }

    private sealed record RecordedRequest(
        Uri? Uri,
        AuthenticationHeaderValue? Authorization);
}
