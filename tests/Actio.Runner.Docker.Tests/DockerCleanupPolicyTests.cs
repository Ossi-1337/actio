using System.Diagnostics;

namespace Actio.Runner.Docker.Tests;

public sealed class DockerCleanupPolicyTests
{
    [Fact]
    public void Evaluate_RemovesProvablyStaleSameInstanceResource()
    {
        var labels = CreateLabels("instance", int.MaxValue, 1);

        Assert.Equal(
            DockerCleanupDecision.Remove,
            DockerCleanupPolicy.Evaluate(labels, "instance"));
    }

    [Fact]
    public void Evaluate_SkipsActiveOwner()
    {
        using var process = Process.GetCurrentProcess();
        var labels = CreateLabels(
            "instance",
            process.Id,
            process.StartTime.ToUniversalTime().Ticks);

        Assert.Equal(
            DockerCleanupDecision.SkipActive,
            DockerCleanupPolicy.Evaluate(labels, "instance"));
    }

    [Fact]
    public void Evaluate_SkipsForeignInstance()
    {
        var labels = CreateLabels("foreign", int.MaxValue, 1);

        Assert.Equal(
            DockerCleanupDecision.SkipForeign,
            DockerCleanupPolicy.Evaluate(labels, "instance"));
    }

    [Fact]
    public void Evaluate_SkipsMalformedOwnership()
    {
        var labels = new Dictionary<string, string>
        {
            ["io.actio.instance"] = "instance",
            ["io.actio.owner-pid"] = "not-a-pid"
        };

        Assert.Equal(
            DockerCleanupDecision.SkipUnverifiable,
            DockerCleanupPolicy.Evaluate(labels, "instance"));
    }

    private static IReadOnlyDictionary<string, string> CreateLabels(
        string instanceId,
        int processId,
        long processStart)
        => new Dictionary<string, string>
        {
            ["io.actio.instance"] = instanceId,
            ["io.actio.owner-pid"] = processId.ToString(),
            ["io.actio.owner-start"] = processStart.ToString()
        };
}
