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
