using Actio.Engine.Execution;

namespace Actio.Runner.Docker.Tests;

public sealed class DockerRunnerProviderTests
{
    [Fact]
    public void BuildShellScript_EnablesStrictModeBeforeUserCommand()
    {
        var script = DockerRunnerProvider.BuildShellScript("sh tests/math_tests.sh | tee test-report.txt");

        Assert.Contains("set -e", script);
        Assert.Contains("if (set -o pipefail) 2>/dev/null; then", script);
        Assert.Contains("set -o pipefail", script);
        Assert.EndsWith("sh tests/math_tests.sh | tee test-report.txt", script.TrimEnd());
    }

    [Fact]
    public void CreateDockerActionStartInfo_RunsImageWithoutShellWrapper()
    {
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>
            {
                ["B"] = "2",
                ["A"] = "1"
            });

        var startInfo = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "alpine:3.20");

        Assert.Equal("docker", startInfo.FileName);
        Assert.Contains("run", args);
        Assert.Contains("actio-test", args);
        Assert.Contains("A=1", args);
        Assert.Contains("B=2", args);
        Assert.True(imageIndex >= 0);
        Assert.Equal(args.Length - 1, imageIndex);
    }

    [Fact]
    public void CreateShellStepStartInfo_AddsAdditionalReadOnlyMounts()
    {
        var actionPath = Path.Combine(Directory.GetCurrentDirectory(), "cached-action");
        var request = new StepExecutionRequest(
            "test",
            "Use action",
            "alpine-latest",
            "echo remote",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            [new StepExecutionMount(actionPath, "/actio/action", ReadOnly: true)]);

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "alpine:3.20", "actio-test");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("-v", args);
        Assert.Contains($"{Path.GetFullPath(actionPath)}:/actio/action:ro", args);
    }
}
