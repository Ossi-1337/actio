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
    public void CreateDockerActionStartInfo_AddsAdditionalWritableMounts()
    {
        var envFilePath = Path.Combine(Directory.GetCurrentDirectory(), "env-files");
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            [new StepExecutionMount(envFilePath, "/actio/env", ReadOnly: false)]);

        var startInfo = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("-v", args);
        Assert.Contains($"{Path.GetFullPath(envFilePath)}:/actio/env", args);
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
            AdditionalMounts: [new StepExecutionMount(actionPath, "/actio/action", ReadOnly: true)]);

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "alpine:3.20", "actio-test");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("-v", args);
        Assert.Contains($"{Path.GetFullPath(actionPath)}:/actio/action:ro", args);
    }

    [Fact]
    public void CreateShellStepStartInfo_UsesConfiguredShellAndWorkingDirectory()
    {
        var request = new StepExecutionRequest(
            "test",
            "Run tests",
            "ubuntu-latest",
            "dotnet test",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Shell: "bash",
            WorkingDirectory: "src/Actio.Core");

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "ubuntu:24.04", "actio-test");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("bash", args);
        Assert.Contains("/workspace/src/Actio.Core", args);
    }

    [Theory]
    [InlineData(null, "/workspace")]
    [InlineData("", "/workspace")]
    [InlineData("src", "/workspace/src")]
    [InlineData("src\\Actio.Core", "/workspace/src/Actio.Core")]
    public void ToContainerWorkingDirectory_MapsRelativePathsInsideWorkspace(
        string? workingDirectory,
        string expected)
    {
        Assert.Equal(expected, DockerRunnerProvider.ToContainerWorkingDirectory(workingDirectory));
    }
}
