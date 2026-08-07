using System.Diagnostics;

namespace Actio.Cli.Tests;

public sealed class WebProcessMetadataStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"actio-web-process-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SaveReadAndOwnedDeleteRoundTrip()
    {
        var store = new WebProcessMetadataStore(_root, "http://127.0.0.1:17345/");
        var metadata = CreateMetadata("instance-a");

        store.Save(metadata);
        store.DeleteIfOwned("instance-b");

        Assert.Equal(metadata, store.Read().Metadata);

        store.DeleteIfOwned("instance-a");
        Assert.Null(store.Read().Metadata);
    }

    [Fact]
    public void ProjectStoreUsesSessionKeyedPathsAndSchemaTwo()
    {
        var projectRoot = Path.Combine(_root, "project");
        var actioHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(projectRoot);
        var session = WebProjectSession.Create(projectRoot, actioHome);
        var store = WebProcessMetadataStore.ForProject(actioHome, session.Id);
        var metadata = CreateMetadata("instance-a") with
        {
            SchemaVersion = 2,
            ProjectRoot = session.ProjectRoot,
            ActioHome = session.ActioHome,
            SessionId = session.Id
        };

        store.Save(metadata);

        Assert.Equal(metadata, store.Read().Metadata);
        Assert.EndsWith(
            Path.Combine("web", "processes", $"{session.Id}.json"),
            store.MetadataPath);
        Assert.EndsWith(
            Path.Combine("web", "locks", $"session-{session.Id}.lock"),
            store.LaunchLockPath);
        Assert.EndsWith(
            Path.Combine("logs", "web", $"{session.Id}.log"),
            store.LogPath);
    }

    [Fact]
    public void ProjectStoreRejectsMetadataForDifferentSessionKey()
    {
        var projectRoot = Path.Combine(_root, "wrong-session-project");
        var actioHome = Path.Combine(_root, "wrong-session-home");
        Directory.CreateDirectory(projectRoot);
        var session = WebProjectSession.Create(projectRoot, actioHome);
        var store = WebProcessMetadataStore.ForProject(actioHome, session.Id);
        var metadata = CreateMetadata("instance-a") with
        {
            SchemaVersion = 2,
            ProjectRoot = session.ProjectRoot,
            ActioHome = session.ActioHome,
            SessionId = new string('a', 24)
        };

        store.Save(metadata);

        Assert.True(store.Read().IsCorrupt);
    }

    [Fact]
    public void ProjectSessionIdentityIsDeterministic()
    {
        var projectRoot = Path.Combine(_root, "identity-project");
        var actioHome = Path.Combine(_root, "identity-home");
        Directory.CreateDirectory(projectRoot);

        var first = WebProjectSession.Create(projectRoot, actioHome);
        var second = WebProjectSession.Create(
            Path.Combine(projectRoot, "."),
            Path.Combine(actioHome, "."));

        Assert.Equal(first, second);
        if (OperatingSystem.IsWindows())
        {
            var differentCase = WebProjectSession.Create(
                projectRoot.ToUpperInvariant(),
                actioHome.ToUpperInvariant());
            Assert.Equal(first.Id, differentCase.Id);
        }
    }

    [Fact]
    public void ProjectSessionIdentityResolvesDirectoryLinks()
    {
        var projectRoot = Path.Combine(_root, "link-project");
        var projectLink = Path.Combine(_root, "link-alias");
        var actioHome = Path.Combine(_root, "link-home");
        Directory.CreateDirectory(projectRoot);
        try
        {
            Directory.CreateSymbolicLink(projectLink, projectRoot);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        var direct = WebProjectSession.Create(projectRoot, actioHome);
        var alias = WebProjectSession.Create(projectLink, actioHome);

        Assert.Equal(direct.Id, alias.Id);
        Assert.Equal(direct.ProjectRoot, alias.ProjectRoot);
    }

    [Fact]
    public void CorruptMetadataCanBeQuarantined()
    {
        var store = new WebProcessMetadataStore(_root, "http://127.0.0.1:17345");
        Directory.CreateDirectory(Path.GetDirectoryName(store.MetadataPath)!);
        File.WriteAllText(store.MetadataPath, "{broken");

        Assert.True(store.Read().IsCorrupt);

        var quarantinePath = store.QuarantineCorrupt();

        Assert.NotNull(quarantinePath);
        Assert.True(File.Exists(quarantinePath));
        Assert.False(File.Exists(store.MetadataPath));
    }

    [Fact]
    public void IncompleteSchemaIsCorrupt()
    {
        var store = new WebProcessMetadataStore(_root, "http://127.0.0.1:17345");
        Directory.CreateDirectory(Path.GetDirectoryName(store.MetadataPath)!);
        File.WriteAllText(store.MetadataPath, """{"SchemaVersion":1,"ProcessId":42}""");

        var result = store.Read();

        Assert.True(result.IsCorrupt);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void OwnerStateUsesPidAndProcessStartIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var startTicks = process.StartTime.ToUniversalTime().Ticks;

        Assert.Equal(
            WebOwnerState.Active,
            WebProcessMetadataStore.GetOwnerState(process.Id, startTicks));
        Assert.Equal(
            WebOwnerState.Stale,
            WebProcessMetadataStore.GetOwnerState(process.Id, startTicks + 1));
        Assert.Equal(
            WebOwnerState.Stale,
            WebProcessMetadataStore.GetOwnerState(int.MaxValue, startTicks));
    }

    [Fact]
    public void ProcessVerificationUsesStartIdentityAndExecutablePath()
    {
        using var process = Process.GetCurrentProcess();
        var metadata = CreateMetadata("instance") with
        {
            ProcessId = process.Id,
            ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            HostPath = Environment.ProcessPath!
        };

        Assert.True(LocalWebServerLauncher.IsVerifiedProcess(process, metadata));
        Assert.False(LocalWebServerLauncher.IsVerifiedProcess(
            process,
            metadata with { ProcessStartTimeUtcTicks = metadata.ProcessStartTimeUtcTicks + 1 }));
        Assert.False(LocalWebServerLauncher.IsVerifiedProcess(
            process,
            metadata with { HostPath = Path.Combine(_root, "different.exe") }));
    }

    [Fact]
    public void UrlKeysAndOwnershipTokensAreStableAndDistinct()
    {
        Assert.Equal(
            WebProcessMetadataStore.CreateUrlKey("HTTP://127.0.0.1:17345/"),
            WebProcessMetadataStore.CreateUrlKey("http://127.0.0.1:17345"));
        Assert.NotEqual(
            WebProcessMetadataStore.CreateUrlKey("http://127.0.0.1:17345"),
            WebProcessMetadataStore.CreateUrlKey("http://127.0.0.1:17346"));

        var token = WebProcessMetadataStore.CreateOwnershipToken();
        Assert.True(WebProcessMetadataStore.FixedTimeEquals(token, token));
        Assert.False(WebProcessMetadataStore.FixedTimeEquals(token, $"{token}x"));
        Assert.False(WebProcessMetadataStore.FixedTimeEquals(token, null));
    }

    [Fact]
    public void LifecycleLogIsBounded()
    {
        var store = new WebProcessMetadataStore(_root, "http://127.0.0.1:17345");

        store.AppendLog(new string('x', 2 * 1024 * 1024));

        Assert.InRange(new FileInfo(store.LogPath).Length, 1, 1024 * 1024);
        Assert.NotEmpty(File.ReadAllText(store.LogPath));
    }

    [Fact]
    public void RuntimeUsageLockAllowsConcurrentWorkersButBlocksCleanup()
    {
        using var first = WebProcessMetadataStore.OpenRuntimeUsageLock(_root, "runtime");
        using var second = WebProcessMetadataStore.OpenRuntimeUsageLock(_root, "runtime");
        var path = WebProcessMetadataStore.GetRuntimeLockPath(_root, "runtime");

        Assert.Throws<IOException>(() =>
            File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    [Fact]
    public async Task WorkerBindingWaitsForProvisionalMetadata()
    {
        var store = new WebProcessMetadataStore(_root, "http://127.0.0.1:17345");
        var publish = CliApplication.PublishWebWorkerBindingAsync(
            store,
            "instance-a",
            "http://127.0.0.1:54321",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await Task.Delay(100);
        store.Save(CreateMetadata("instance-a"));

        var metadata = await publish;

        Assert.Equal("http://127.0.0.1:54321", metadata.Url);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WebProcessMetadata CreateMetadata(string instanceId)
    {
        return new WebProcessMetadata(
            1,
            Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            instanceId,
            WebProcessMetadataStore.CreateOwnershipToken(),
            "runtime",
            Path.Combine(_root, "snapshot"),
            Path.Combine(_root, "snapshot", "actio.exe"),
            "1.0.0",
            "http://127.0.0.1:17345",
            _root,
            _root,
            DateTimeOffset.UtcNow);
    }
}
