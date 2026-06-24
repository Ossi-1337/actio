using Actio.Engine.Actions;
using Actio.Storage;

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
}
