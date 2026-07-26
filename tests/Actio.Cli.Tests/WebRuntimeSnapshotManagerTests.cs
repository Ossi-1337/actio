namespace Actio.Cli.Tests;

public sealed class WebRuntimeSnapshotManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"actio-web-runtime-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DescribeCurrentProducesDeterministicContentIdentity()
    {
        var source = CreateRuntimeSource();
        var manager = CreateManager(source);

        var first = manager.DescribeCurrent();
        var second = manager.DescribeCurrent();

        Assert.Equal(first.Identity, second.Identity);
        Assert.Equal(first.Files, second.Files);

        File.WriteAllText(Path.Combine(source, "dependency.dll"), "changed");

        var changed = manager.DescribeCurrent();
        Assert.NotEqual(first.Identity, changed.Identity);
    }

    [Fact]
    public async Task PrepareCreatesAndConcurrentlyReusesValidatedSnapshot()
    {
        var source = CreateRuntimeSource();
        var actioHome = Path.Combine(_root, "home");
        var manager = CreateManager(source);

        var snapshots = await Task.WhenAll(
            Task.Run(() => manager.Prepare(actioHome)),
            Task.Run(() => manager.Prepare(actioHome)));

        Assert.Equal(snapshots[0].Identity, snapshots[1].Identity);
        Assert.Equal(snapshots[0].RootPath, snapshots[1].RootPath);
        Assert.True(File.Exists(Path.Combine(snapshots[0].RootPath, "runtime.json")));
        Assert.Equal(
            "entry",
            File.ReadAllText(Path.Combine(snapshots[0].RootPath, "actio.dll")));
    }

    [Fact]
    public void PrepareRejectsTamperedExistingSnapshot()
    {
        var source = CreateRuntimeSource();
        var manager = CreateManager(source);
        var snapshot = manager.Prepare(Path.Combine(_root, "home"));
        File.WriteAllText(snapshot.EntryAssemblyPath, "tampered");

        var exception = Assert.Throws<IOException>(
            () => manager.Prepare(Path.Combine(_root, "home")));

        Assert.Contains("does not match its content identity", exception.Message);
    }

    [Fact]
    public void PrepareRejectsUnexpectedSnapshotFiles()
    {
        var source = CreateRuntimeSource();
        var manager = CreateManager(source);
        var actioHome = Path.Combine(_root, "home");
        var snapshot = manager.Prepare(actioHome);
        File.WriteAllText(Path.Combine(snapshot.RootPath, "injected.dll"), "unexpected");

        var exception = Assert.Throws<IOException>(() => manager.Prepare(actioHome));

        Assert.Contains("does not match its content identity", exception.Message);
    }

    [Fact]
    public void DescribeCurrentRejectsEntrypointOutsideRuntimeRoot()
    {
        var source = CreateRuntimeSource();
        var outsideEntrypoint = Path.Combine(_root, "outside.dll");
        File.WriteAllText(outsideEntrypoint, "outside");
        var manager = new WebRuntimeSnapshotManager(
            source,
            Path.Combine(source, "actio.exe"),
            outsideEntrypoint,
            "1.0.0");

        var exception = Assert.Throws<InvalidOperationException>(() => manager.DescribeCurrent());

        Assert.Contains("must stay inside runtime source", exception.Message);
    }

    [Fact]
    public void PrepareRejectsSnapshotStorageInsideRuntimeSource()
    {
        var source = CreateRuntimeSource();
        var manager = CreateManager(source);

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.Prepare(Path.Combine(source, "local-home")));

        Assert.Contains("cannot be inside runtime source", exception.Message);
    }

    [Fact]
    public void CleanupPreservesCurrentProtectedAndLockedSnapshots()
    {
        var actioHome = Path.Combine(_root, "home");
        var runtimes = Path.Combine(actioHome, "web", "runtimes");
        Directory.CreateDirectory(runtimes);
        foreach (var name in new[] { "current", "protected", "locked", "stale" })
        {
            Directory.CreateDirectory(Path.Combine(runtimes, name));
        }

        var lockedPath = WebProcessMetadataStore.GetRuntimeLockPath(actioHome, "locked");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        using var runtimeLock = File.Open(
            lockedPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var manager = CreateManager(CreateRuntimeSource());
        var result = manager.Cleanup(
            actioHome,
            "current",
            new HashSet<string>(["protected"], StringComparer.Ordinal),
            TimeSpan.Zero);

        Assert.Equal(1, result.Removed);
        Assert.True(Directory.Exists(Path.Combine(runtimes, "current")));
        Assert.True(Directory.Exists(Path.Combine(runtimes, "protected")));
        Assert.True(Directory.Exists(Path.Combine(runtimes, "locked")));
        Assert.False(Directory.Exists(Path.Combine(runtimes, "stale")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateRuntimeSource()
    {
        var source = Path.Combine(_root, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "actio.dll"), "entry");
        File.WriteAllText(Path.Combine(source, "actio.exe"), "host");
        File.WriteAllText(Path.Combine(source, "dependency.dll"), "dependency");
        return source;
    }

    private static WebRuntimeSnapshotManager CreateManager(string source)
    {
        return new WebRuntimeSnapshotManager(
            source,
            Path.Combine(source, "actio.exe"),
            Path.Combine(source, "actio.dll"),
            "1.0.0");
    }
}
