using Actio.Engine.Execution;

namespace Actio.Runner.Docker.Tests;

public sealed class DockerPreflightEvaluatorTests
{
    [Fact]
    public void Evaluate_AdaptsBuiltInCpuAndMemoryDefaultsToDaemonCapacity()
    {
        var result = DockerPreflightEvaluator.Evaluate(
            CreateDockerInfo(cpu: 1, memoryBytes: 1024L * 1024 * 1024),
            CreateRequest(RunnerSecurityProfiles.SecureBaseline));

        Assert.True(result.Success);
        Assert.Equal(1, result.Limits.Cpu);
        Assert.Equal(768L * 1024 * 1024, result.Limits.MemoryBytes);
    }

    [Fact]
    public void Evaluate_RejectsExplicitLimitsAboveDaemonCapacity()
    {
        var request = CreateRequest(
            RunnerSecurityProfiles.SecureBaseline,
            new ContainerResourceConfiguration(Cpu: 3, MemoryMiB: 2048));

        var result = DockerPreflightEvaluator.Evaluate(
            CreateDockerInfo(cpu: 2, memoryBytes: 1024L * 1024 * 1024),
            request);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("CPU limit", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("memory limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_StrictAcceptsDockerDesktopAndRequiresSwap()
    {
        var result = DockerPreflightEvaluator.Evaluate(
            CreateDockerInfo(swap: false),
            CreateRequest(RunnerSecurityProfiles.Strict));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("swap-limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_PublishedPortsRequireEngine28()
    {
        var request = CreateRequest(RunnerSecurityProfiles.SecureBaseline) with
        {
            HasPublishedPorts = true
        };

        var result = DockerPreflightEvaluator.Evaluate(
            CreateDockerInfo(version: "27.5.1"),
            request);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Engine 28", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_RejectsExplicitTempLimitAboveHalfEffectiveMemory()
    {
        var request = CreateRequest(
            RunnerSecurityProfiles.SecureBaseline,
            new ContainerResourceConfiguration(MemoryMiB: 512, TempMiB: 512));

        var result = DockerPreflightEvaluator.Evaluate(CreateDockerInfo(), request);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("half", StringComparison.Ordinal));
    }

    private static RunnerPreflightRequest CreateRequest(
        string profile,
        ContainerResourceConfiguration? configuration = null)
        => new(
            "run-1",
            new RunnerExecutionPolicy(
                profile,
                configuration ?? new ContainerResourceConfiguration(),
                new ActioInstanceIdentity("instance", 1, 1)),
            false);

    private static string CreateDockerInfo(
        double cpu = 4,
        long memoryBytes = 8L * 1024 * 1024 * 1024,
        bool swap = true,
        string version = "29.0.0")
        => $$"""
        {
          "ServerVersion": "{{version}}",
          "OperatingSystem": "Docker Desktop",
          "OSType": "linux",
          "NCPU": {{cpu}},
          "MemTotal": {{memoryBytes}},
          "CpuCfsQuota": true,
          "MemoryLimit": true,
          "SwapLimit": {{swap.ToString().ToLowerInvariant()}},
          "PidsLimit": true,
          "CgroupVersion": "2",
          "CgroupDriver": "cgroupfs",
          "Plugins": { "Log": ["local", "json-file"] },
          "SecurityOptions": ["name=seccomp,profile=builtin"]
        }
        """;
}
