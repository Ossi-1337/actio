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
    public void CreateDockerActionStartInfo_UsesEntrypointAndArguments()
    {
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            EntryPoint: "/bin/echo",
            Arguments: ["hello world", "--count", "2"]);

        var startInfo = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var entryPointIndex = Array.IndexOf(args, "--entrypoint");
        var imageIndex = Array.IndexOf(args, "alpine:3.20");

        Assert.True(entryPointIndex >= 0);
        Assert.Equal("/bin/echo", args[entryPointIndex + 1]);
        Assert.True(imageIndex > entryPointIndex);
        Assert.Equal(["hello world", "--count", "2"], args[(imageIndex + 1)..]);
    }

    [Fact]
    public void CreateJavaScriptActionStartInfo_RunsNode20WithActionScript()
    {
        var actionPath = Path.Combine(Directory.GetCurrentDirectory(), "cached-action");
        var request = new JavaScriptActionExecutionRequest(
            "test",
            "Use JavaScript action",
            Directory.GetCurrentDirectory(),
            "/actio/action",
            "dist/index.js",
            new Dictionary<string, string>
            {
                ["INPUT_NAME"] = "Actio"
            },
            [new StepExecutionMount(actionPath, "/actio/action", ReadOnly: true)]);

        var startInfo = DockerRunnerProvider.CreateJavaScriptActionStartInfo(request, "dist/index.js", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "node:20-bookworm-slim");

        Assert.Equal("docker", startInfo.FileName);
        Assert.Contains("run", args);
        Assert.Contains("actio-test", args);
        Assert.Contains("INPUT_NAME=Actio", args);
        Assert.Contains($"{Path.GetFullPath(actionPath)}:/actio/action:ro", args);
        Assert.True(imageIndex >= 0);
        Assert.Equal("node", args[imageIndex + 1]);
        Assert.Equal("/actio/action/dist/index.js", args[imageIndex + 2]);
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
    public void CreateShellStepStartInfo_UsesJobContainerConfiguration()
    {
        var cachePath = Path.Combine(Directory.GetCurrentDirectory(), ".actio", "cache");
        var request = new StepExecutionRequest(
            "test",
            "Run npm",
            "ubuntu-latest",
            "npm test",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>
            {
                ["NODE_ENV"] = "test"
            },
            Container: new JobContainerExecutionOptions(
                "node:22",
                ["3000:3000"],
                ["--cpus", "1", "--init"],
                [new StepExecutionMount(cachePath, "/cache", ReadOnly: true)]));

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "node:22", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "node:22");

        Assert.Contains("3000:3000", args);
        Assert.Contains("--cpus", args);
        Assert.Contains("1", args);
        Assert.Contains("--init", args);
        Assert.Contains($"{Path.GetFullPath(cachePath)}:/cache:ro", args);
        Assert.Contains("NODE_ENV=test", args);
        Assert.True(imageIndex >= 0);
        Assert.Equal("sh", args[imageIndex + 1]);
        Assert.Equal("-lc", args[imageIndex + 2]);
    }

    [Fact]
    public void CreateShellStepStartInfo_AttachesServiceNetwork()
    {
        var request = new StepExecutionRequest(
            "test",
            "Run tests",
            "ubuntu-latest",
            "dotnet test",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Services: new JobServiceNetwork("actio-test-network", ["actio-postgres"]));

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "ubuntu:24.04", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var networkIndex = Array.IndexOf(args, "--network");

        Assert.True(networkIndex >= 0);
        Assert.Equal("actio-test-network", args[networkIndex + 1]);
    }

    [Fact]
    public void CreateDockerActionStartInfo_AttachesServiceNetwork()
    {
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Services: new JobServiceNetwork("actio-test-network", ["actio-postgres"]));

        var startInfo = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var networkIndex = Array.IndexOf(args, "--network");

        Assert.True(networkIndex >= 0);
        Assert.Equal("actio-test-network", args[networkIndex + 1]);
    }

    [Fact]
    public void CreateServiceContainerStartInfo_UsesServiceConfiguration()
    {
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "db");
        var service = new ServiceContainerDefinition(
            "postgres",
            "postgres:16",
            new Dictionary<string, string>
            {
                ["POSTGRES_PASSWORD"] = "postgres"
            },
            ["5432:5432"],
            ["--health-cmd=pg_isready", "--health-interval=5s"],
            [new StepExecutionMount(dbPath, "/var/lib/postgresql/data", ReadOnly: false)]);
        var request = new ServiceContainerStartRequest(
            "test",
            Directory.GetCurrentDirectory(),
            [service]);

        var startInfo = DockerRunnerProvider.CreateServiceContainerStartInfo(
            request,
            service,
            "actio-test-network",
            "actio-postgres");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "postgres:16");

        Assert.Equal("docker", startInfo.FileName);
        Assert.Contains("-d", args);
        Assert.Contains("actio-postgres", args);
        Assert.Contains("actio-test-network", args);
        Assert.Contains("postgres", args);
        Assert.Contains("5432:5432", args);
        Assert.Contains("--health-cmd=pg_isready", args);
        Assert.Contains("--health-interval=5s", args);
        Assert.Contains($"{Path.GetFullPath(dbPath)}:/var/lib/postgresql/data", args);
        Assert.Contains("POSTGRES_PASSWORD=postgres", args);
        Assert.Equal(args.Length - 1, imageIndex);
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

    [Theory]
    [InlineData("/actio/action", "dist/index.js", "/actio/action/dist/index.js")]
    [InlineData("/actio/action/", "./dist/index.js", "/actio/action/dist/index.js")]
    [InlineData("/actio/action", "dist\\index.js", "/actio/action/dist/index.js")]
    public void ToActionContainerPath_MapsActionScriptsInsideActionMount(
        string actionPath,
        string scriptPath,
        string expected)
    {
        Assert.Equal(expected, DockerRunnerProvider.ToActionContainerPath(actionPath, scriptPath));
    }
}
