using Actio.Engine.Actions;
using Actio.Storage;
using System.IO.Compression;

namespace Actio.Storage.Tests;

public sealed class FileSystemActionCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-action-cache-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetOrAddLocalActionAsync_ReusesEntryForSameSourceAndHash()
    {
        var cache = new FileSystemActionCache(_root);
        var request = new LocalActionCacheRequest("./.actio/actions/hello", "C:\\repo\\.actio\\actions\\hello\\action.yml", "abc123");

        var first = await cache.GetOrAddLocalActionAsync(request);
        var second = await cache.GetOrAddLocalActionAsync(request);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.True(second.LastUsedAt >= first.LastUsedAt);
        Assert.True(File.Exists(Path.Combine(second.CachePath, "action.json")));
    }

    [Fact]
    public async Task ListAsync_ReturnsEntries()
    {
        var cache = new FileSystemActionCache(_root);
        await cache.GetOrAddLocalActionAsync(new LocalActionCacheRequest("./action", "C:\\repo\\action.yml", "hash"));

        var entries = await cache.ListAsync();

        var entry = Assert.Single(entries);
        Assert.Equal("local", entry.Kind);
        Assert.Equal("./action", entry.Uses);
    }

    [Fact]
    public async Task GetOrAddDockerImageActionAsync_WritesMutableDockerEntry()
    {
        var cache = new FileSystemActionCache(_root);
        var request = new DockerImageActionCacheRequest("docker://alpine:3.20", "alpine:3.20", IsPinned: false, MutablePart: "3.20");

        var entry = await cache.GetOrAddDockerImageActionAsync(request);

        Assert.Equal("docker", entry.Kind);
        Assert.Equal("docker://alpine:3.20", entry.Uses);
        Assert.Equal("alpine:3.20", entry.SourcePath);
        Assert.Equal("3.20", entry.MutablePart);
        Assert.Null(entry.PinnedIdentity);
        Assert.Contains(Path.Combine("cache", "actions", "docker"), entry.CachePath);
        Assert.True(File.Exists(Path.Combine(entry.CachePath, "action.json")));
    }

    [Fact]
    public async Task GetOrAddDockerImageActionAsync_RecordsPinnedDigest()
    {
        var cache = new FileSystemActionCache(_root);
        var digest = new string('a', 64);
        var image = $"alpine@sha256:{digest}";
        var request = new DockerImageActionCacheRequest($"docker://{image}", image, IsPinned: true, MutablePart: null);

        var entry = await cache.GetOrAddDockerImageActionAsync(request);

        Assert.Equal("docker", entry.Kind);
        Assert.Equal(image, entry.PinnedIdentity);
        Assert.Null(entry.MutablePart);
    }

    [Fact]
    public async Task GetGitHubActionSourceAsync_DownloadsAndCachesCompositeActionSource()
    {
        var sha = new string('a', 40);
        var client = new FakeGitHubActionClient([sha, sha], WriteGitHubActionArchive);
        var cache = new FileSystemActionCache(_root, client);
        var request = new GitHubActionSourceRequest(
            "owner/repo/action@v1",
            "owner",
            "repo",
            "action",
            "v1",
            IsPinned: false,
            MutablePart: "v1");

        var first = await cache.GetGitHubActionSourceAsync(request);
        var second = await cache.GetGitHubActionSourceAsync(request);

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Errors));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Errors));
        Assert.Equal(2, client.ResolveCalls);
        Assert.Equal(1, client.DownloadCalls);
        Assert.True(File.Exists(first.ActionFilePath));
        Assert.Equal(first.ActionFilePath, second.ActionFilePath);
        Assert.Equal("github", first.CacheEntry!.Kind);
        Assert.Equal(sha, first.CacheEntry.PinnedIdentity);
        Assert.Equal("v1", first.CacheEntry.MutablePart);
        Assert.Contains(Path.Combine("cache", "actions", "github"), first.CacheEntry.CachePath);
    }

    [Fact]
    public async Task GetGitHubActionSourceAsync_UsesNewCacheEntryWhenMutableRefMoves()
    {
        var firstSha = new string('a', 40);
        var secondSha = new string('b', 40);
        var client = new FakeGitHubActionClient([firstSha, secondSha], WriteGitHubActionArchive);
        var cache = new FileSystemActionCache(_root, client);
        var request = new GitHubActionSourceRequest(
            "owner/repo/action@main",
            "owner",
            "repo",
            "action",
            "main",
            IsPinned: false,
            MutablePart: "main");

        var first = await cache.GetGitHubActionSourceAsync(request);
        var second = await cache.GetGitHubActionSourceAsync(request);

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Errors));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Errors));
        Assert.Equal(2, client.DownloadCalls);
        Assert.NotEqual(first.CacheEntry!.Key, second.CacheEntry!.Key);
        Assert.Equal(firstSha, first.CacheEntry.PinnedIdentity);
        Assert.Equal(secondSha, second.CacheEntry.PinnedIdentity);
    }

    [Fact]
    public async Task GetGitHubActionSourceAsync_RecordsPinnedCommitWithoutResolvingRef()
    {
        var sha = new string('c', 40);
        var client = new FakeGitHubActionClient([], WriteGitHubActionArchive);
        var cache = new FileSystemActionCache(_root, client);
        var request = new GitHubActionSourceRequest(
            $"owner/repo/action@{sha}",
            "owner",
            "repo",
            "action",
            sha,
            IsPinned: true,
            MutablePart: null);

        var result = await cache.GetGitHubActionSourceAsync(request);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(0, client.ResolveCalls);
        Assert.Equal(1, client.DownloadCalls);
        Assert.Equal(sha, result.CacheEntry!.PinnedIdentity);
        Assert.Null(result.CacheEntry.MutablePart);
    }

    [Fact]
    public async Task GetGitHubActionSourceAsync_FailsWhenActionFileIsMissing()
    {
        var sha = new string('d', 40);
        var client = new FakeGitHubActionClient([sha], WriteArchiveWithoutAction);
        var cache = new FileSystemActionCache(_root, client);
        var request = new GitHubActionSourceRequest(
            "owner/repo/action@v1",
            "owner",
            "repo",
            "action",
            "v1",
            IsPinned: false,
            MutablePart: "v1");

        var result = await cache.GetGitHubActionSourceAsync(request);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("action.yml or action.yaml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetOrAddLocalActionAsync_RecreatesCorruptedEntry()
    {
        var cache = new FileSystemActionCache(_root);
        var request = new LocalActionCacheRequest("./action", "C:\\repo\\action.yml", "hash");
        var first = await cache.GetOrAddLocalActionAsync(request);
        await File.WriteAllTextAsync(Path.Combine(first.CachePath, "action.json"), "not json");

        var second = await cache.GetOrAddLocalActionAsync(request);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal("./action", second.Uses);
    }


    [Fact]
    public async Task CleanAsync_RemovesEntries()
    {
        var cache = new FileSystemActionCache(_root);
        await cache.GetOrAddLocalActionAsync(new LocalActionCacheRequest("./action", "C:\\repo\\action.yml", "hash"));

        var removed = await cache.CleanAsync();
        var entries = await cache.ListAsync();

        Assert.Equal(1, removed);
        Assert.Empty(entries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteGitHubActionArchive(string destinationPath)
    {
        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("owner-repo/action/action.yml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(
            """
            name: Remote action
            runs:
              using: composite
              steps:
                - name: Run
                  run: echo remote
            """);
    }

    private static void WriteArchiveWithoutAction(string destinationPath)
    {
        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("owner-repo/action/README.md");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("# repo");
    }

    private sealed class FakeGitHubActionClient : IGitHubActionClient
    {
        private readonly Queue<string> _resolvedShas;
        private readonly Action<string> _writeArchive;

        public FakeGitHubActionClient(IEnumerable<string> resolvedShas, Action<string> writeArchive)
        {
            _resolvedShas = new Queue<string>(resolvedShas);
            _writeArchive = writeArchive;
        }

        public int ResolveCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public Task<string> ResolveCommitShaAsync(
            string owner,
            string repository,
            string reference,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult(_resolvedShas.Dequeue());
        }

        public Task DownloadArchiveAsync(
            string owner,
            string repository,
            string commitSha,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            _writeArchive(destinationPath);
            return Task.CompletedTask;
        }
    }
}
