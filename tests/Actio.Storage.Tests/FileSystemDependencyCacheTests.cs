using Actio.Engine.Caching;
using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class FileSystemDependencyCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-dependency-cache-tests-{Guid.NewGuid():N}");
    private readonly string _projectRoot;

    public FileSystemDependencyCacheTests()
    {
        _projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_projectRoot);
    }

    [Fact]
    public async Task SaveAsync_StoresAndRestoresExactCacheEntry()
    {
        var cache = new FileSystemDependencyCache(_root);
        var packagePath = Path.Combine(_projectRoot, ".nuget", "packages");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");

        var save = await cache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "nuget-main", [".nuget/packages"]));
        Directory.Delete(packagePath, recursive: true);

        var restore = await cache.RestoreAsync(new DependencyCacheRestoreRequest(_projectRoot, "nuget-main", [], [".nuget/packages"]));

        Assert.True(save.Success, string.Join(Environment.NewLine, save.Errors));
        Assert.True(save.Saved);
        Assert.True(restore.Success, string.Join(Environment.NewLine, restore.Errors));
        Assert.True(restore.CacheHit);
        Assert.Equal("nuget-main", restore.MatchedKey);
        Assert.Equal("cached", await File.ReadAllTextAsync(Path.Combine(packagePath, "package.txt")));
    }

    [Fact]
    public async Task RestoreAsync_UsesRestoreKeyPrefixWhenExactKeyIsMissing()
    {
        var cache = new FileSystemDependencyCache(_root);
        var packagePath = Path.Combine(_projectRoot, ".nuget", "packages");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");

        await cache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "nuget-main-abc", [".nuget/packages"]));
        Directory.Delete(packagePath, recursive: true);

        var restore = await cache.RestoreAsync(
            new DependencyCacheRestoreRequest(_projectRoot, "nuget-feature-def", ["nuget-main-"], [".nuget/packages"]));

        Assert.True(restore.Success, string.Join(Environment.NewLine, restore.Errors));
        Assert.False(restore.CacheHit);
        Assert.Equal("nuget-main-abc", restore.MatchedKey);
        Assert.Equal("nuget-main-", restore.MatchedRestoreKey);
        Assert.Equal("cached", await File.ReadAllTextAsync(Path.Combine(packagePath, "package.txt")));
    }

    [Fact]
    public async Task SaveAsync_RejectsPathsOutsideWorkspace()
    {
        var cache = new FileSystemDependencyCache(_root);

        var result = await cache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "outside", ["../outside"]));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("must stay inside the workspace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAsync_RejectsPathsThatIncludeDependencyCacheStorage()
    {
        var actioHome = Path.Combine(_projectRoot, ".actio-home");
        var cache = new FileSystemDependencyCache(actioHome);
        Directory.CreateDirectory(Path.Combine(actioHome, "cache", "dependencies"));

        var result = await cache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "workspace", ["."]));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("cannot include Actio dependency cache storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAsync_RejectsLinksBeforeCreatingCacheEntry()
    {
        var source = Path.Combine(_projectRoot, "packages");
        var outside = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(outside, "host-secret");

        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(source, "linked.txt"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var cache = new FileSystemDependencyCache(Path.Combine(_root, "home"));
            var result = await cache.SaveAsync(
                new DependencyCacheSaveRequest(_projectRoot, "unsafe", ["packages"]));

            Assert.False(result.Success);
            Assert.Contains(result.Errors, error => error.Contains("linked.txt", StringComparison.Ordinal));
            Assert.Empty(await cache.ListAsync());
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task CleanAsync_RemovesDependencyCacheEntries()
    {
        var cache = new FileSystemDependencyCache(_root);
        var packagePath = Path.Combine(_projectRoot, ".nuget", "packages");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");
        await cache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "nuget-main", [".nuget/packages"]));

        var removed = await cache.CleanAsync();

        Assert.Equal(1, removed);
        Assert.Empty(await cache.ListAsync());
    }

    [Fact]
    public async Task CleanAsync_IgnoresIncompleteTemporaryEntries()
    {
        var cache = new FileSystemDependencyCache(_root);
        var temporaryPath = Path.Combine(cache.DependencyCachePath, ".tmp-active");
        Directory.CreateDirectory(temporaryPath);
        await File.WriteAllTextAsync(Path.Combine(temporaryPath, "cache.json"), "{}");

        var removed = await cache.CleanAsync();

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(temporaryPath, "cache.json")));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentWritersReuseTheFirstEntry()
    {
        var packagePath = Path.Combine(_projectRoot, ".nuget", "packages");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");
        var request = new DependencyCacheSaveRequest(
            _projectRoot,
            "shared-key",
            [".nuget/packages"]);
        var caches = Enumerable.Range(0, 8)
            .Select(_ => new FileSystemDependencyCache(_root))
            .ToArray();

        var results = await Task.WhenAll(caches.Select(cache => cache.SaveAsync(request)));

        Assert.All(results, result => Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors)));
        Assert.Equal(1, results.Count(result => result.Saved));
        Assert.Equal(7, results.Count(result => !result.Saved));
        Assert.Single(await caches[0].ListAsync());
    }

    [Fact]
    public async Task ListAsync_CanPollWhileEntryUsageMetadataIsUpdated()
    {
        var packagePath = Path.Combine(_projectRoot, "packages-poll");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");
        var cache = new FileSystemDependencyCache(_root);
        await cache.SaveAsync(new DependencyCacheSaveRequest(
            _projectRoot,
            "poll-key",
            ["packages-poll"]));
        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 100; index++)
            {
                await cache.RestoreAsync(new DependencyCacheRestoreRequest(
                    _projectRoot,
                    "poll-key",
                    [],
                    ["packages-poll"]));
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                await cache.ListAsync();
            }
        }));

        await Task.WhenAll(readers.Append(writer));

        Assert.Single(await cache.ListAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
