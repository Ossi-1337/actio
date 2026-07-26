using System.Text.Json;
using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class FileSystemActioConfigurationProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"actio-config-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Load_ReturnsDefaultsAndStableInstanceIdentityWhenConfigIsMissing()
    {
        var provider = new FileSystemActioConfigurationProvider(_root);

        var first = provider.Load();
        var second = provider.Load();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Null(first.Configuration.Cpu);
        Assert.Equal(first.InstanceIdentity.InstanceId, second.InstanceIdentity.InstanceId);
        Assert.True(Guid.TryParse(first.InstanceIdentity.InstanceId, out _));
    }

    [Fact]
    public void Validate_DoesNotCreateActioHomeOrInstanceIdentity()
    {
        var result = new FileSystemActioConfigurationProvider(_root).Validate();

        Assert.True(result.Success);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Validate_RejectsInvalidConfigWithoutCreatingInstanceIdentity()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "config.json"),
            """{ "resources": { "cpu": 9 } }""");

        var result = new FileSystemActioConfigurationProvider(_root).Validate();

        Assert.False(result.Success);
        Assert.Contains("resources.cpu", Assert.Single(result.Errors));
        Assert.False(File.Exists(Path.Combine(_root, "instance-id")));
    }

    [Fact]
    public void Load_ReadsResourceConfiguration()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "config.json"),
            """
            {
              "resources": {
                "cpu": 1.5,
                "memoryMiB": 2048,
                "pids": 256,
                "tempMiB": 128,
                "dockerLogMiB": 5,
                "dockerLogFiles": 2,
                "stepLogMiB": 25
              }
            }
            """);

        var result = new FileSystemActioConfigurationProvider(_root).Load();

        Assert.True(result.Success);
        Assert.Equal(1.5, result.Configuration.Cpu);
        Assert.Equal(2048, result.Configuration.MemoryMiB);
        Assert.Equal(25, result.Configuration.StepLogMiB);
    }

    [Fact]
    public void Load_RejectsUnknownKeys()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "config.json"),
            """{ "resources": { "privileged": true } }""");

        var result = new FileSystemActioConfigurationProvider(_root).Load();

        Assert.False(result.Success);
        Assert.Contains("configuration could not be loaded", Assert.Single(result.Errors));
    }

    [Fact]
    public void Load_RejectsValuesOutsideSafeBounds()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "config.json"),
            """{ "resources": { "cpu": 9 } }""");

        var result = new FileSystemActioConfigurationProvider(_root).Load();

        Assert.False(result.Success);
        Assert.Contains("resources.cpu", Assert.Single(result.Errors));
    }

    [Fact]
    public void Load_RejectsUnboundedPidAndLogSettings()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "config.json"),
            """
            {
              "resources": {
                "pids": 4097,
                "dockerLogFiles": 6
              }
            }
            """);

        var result = new FileSystemActioConfigurationProvider(_root).Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("resources.pids", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("resources.dockerLogFiles", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
