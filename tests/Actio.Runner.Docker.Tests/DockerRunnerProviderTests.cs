using Actio.Core.Workflows;
using Actio.Engine.Execution;
using System.Diagnostics;

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
        AssertSecureBaseline(args);
        Assert.True(imageIndex >= 0);
        Assert.Equal(args.Length - 1, imageIndex);
    }

    [Fact]
    public void CreateDockerActionStartInfo_AppliesResourcesOwnershipAndStrictControls()
    {
        var execution = new RunnerExecutionContext(
            "run-1",
            RunnerSecurityProfiles.Strict,
            RunnerSecurityProfiles.Strict,
            new ContainerResourceLimits(
                1.5,
                1024L * 1024 * 1024,
                256,
                128L * 1024 * 1024,
                5L * 1024 * 1024,
                2,
                25L * 1024 * 1024),
            new ActioInstanceIdentity("instance-1", 42, 1234));
        var runtime = new JobRuntimeContext(
            "none",
            [],
            Execution: execution,
            OwnsNetwork: false);
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Runtime: runtime);

        var args = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test")
            .ArgumentList
            .ToArray();

        AssertOptionValue(args, "--cpus", "1.5");
        AssertOptionValue(args, "--memory", "1073741824");
        AssertOptionValue(args, "--pids-limit", "256");
        Assert.Contains("--cap-drop", args);
        Assert.Contains("ALL", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("io.actio.instance=instance-1", args);
        AssertOptionValue(args, "--network", "none");
    }

    [Fact]
    public void CreateDockerActionStartInfo_AddsAdditionalWritableMounts()
    {
        var envFilePath = Path.Combine(Directory.GetCurrentDirectory(), "env-files");
        Directory.CreateDirectory(envFilePath);
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            [new StepExecutionMount(envFilePath, "/actio/env", ReadOnly: false, StepExecutionMountKind.EnvironmentFiles)]);

        var startInfo = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("--mount", args);
        Assert.Contains($"type=bind,src={Path.GetFullPath(envFilePath)},dst=/actio/env", args);
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
    public void CreateDockerfileActionBuildStartInfo_BuildsTaggedActionImage()
    {
        var actionRoot = Path.Combine(Directory.GetCurrentDirectory(), "cached-action");
        var dockerfilePath = Path.Combine(actionRoot, "Dockerfile");
        var request = new DockerfileActionExecutionRequest(
            "test",
            "Use Dockerfile action",
            "actio/action:abc123",
            Directory.GetCurrentDirectory(),
            actionRoot,
            dockerfilePath,
            new Dictionary<string, string>());

        var startInfo = DockerRunnerProvider.CreateDockerfileActionBuildStartInfo(request);
        var args = startInfo.ArgumentList.ToArray();

        Assert.Equal("docker", startInfo.FileName);
        Assert.Equal("build", args[0]);
        Assert.Contains("actio=true", args);
        Assert.Contains("actio.job=test", args);
        Assert.Contains("actio.step=Use Dockerfile action", args);
        var tagIndex = Array.IndexOf(args, "-t");
        Assert.True(tagIndex >= 0);
        Assert.Equal("actio/action:abc123", args[tagIndex + 1]);
        var dockerfileIndex = Array.IndexOf(args, "-f");
        Assert.True(dockerfileIndex >= 0);
        Assert.Equal(Path.GetFullPath(dockerfilePath), args[dockerfileIndex + 1]);
        Assert.Equal(Path.GetFullPath(actionRoot), args[^1]);
    }

    [Fact]
    public void CreateJavaScriptActionStartInfo_RunsNode20WithActionScript()
    {
        var actionPath = Path.Combine(Directory.GetCurrentDirectory(), "cached-action");
        Directory.CreateDirectory(actionPath);
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
            [new StepExecutionMount(actionPath, "/actio/action", ReadOnly: true, StepExecutionMountKind.ActionSource)]);

        var startInfo = DockerRunnerProvider.CreateJavaScriptActionStartInfo(request, "dist/index.js", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "node:20-bookworm-slim");

        Assert.Equal("docker", startInfo.FileName);
        Assert.Contains("run", args);
        Assert.Contains("actio-test", args);
        Assert.Contains("INPUT_NAME=Actio", args);
        Assert.Contains($"type=bind,src={Path.GetFullPath(actionPath)},dst=/actio/action,readonly", args);
        AssertSecureBaseline(args);
        Assert.True(imageIndex >= 0);
        Assert.Equal("node", args[imageIndex + 1]);
        Assert.Equal("/actio/action/dist/index.js", args[imageIndex + 2]);
    }

    [Fact]
    public void CreateShellStepStartInfo_AddsAdditionalReadOnlyMounts()
    {
        var actionPath = Path.Combine(Directory.GetCurrentDirectory(), "cached-action");
        Directory.CreateDirectory(actionPath);
        var request = new StepExecutionRequest(
            "test",
            "Use action",
            "alpine-latest",
            "echo remote",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            AdditionalMounts: [new StepExecutionMount(actionPath, "/actio/action", ReadOnly: true, StepExecutionMountKind.ActionSource)]);

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "alpine:3.20", "actio-test");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("--mount", args);
        Assert.Contains($"type=bind,src={Path.GetFullPath(actionPath)},dst=/actio/action,readonly", args);
    }

    [Fact]
    public void CreateShellStepStartInfo_UsesJobContainerConfiguration()
    {
        var cachePath = Path.Combine(Directory.GetCurrentDirectory(), ".actio", "cache");
        Directory.CreateDirectory(cachePath);
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
                [new ContainerPortMapping(3000, 3000)],
                ["--cpus", "1", "--memory", "512m", "--init"],
                [new StepExecutionMount(cachePath, "/cache", ReadOnly: true)]));

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "node:22", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "node:22");

        Assert.Contains("127.0.0.1:3000:3000/tcp", args);
        Assert.Contains("--cpus", args);
        Assert.Contains("1", args);
        Assert.Contains("--init", args);
        AssertOptionValue(args, "--memory-swap", "536870912");
        Assert.Contains($"type=bind,src={Path.GetFullPath(cachePath)},dst=/cache,readonly", args);
        Assert.Contains("NODE_ENV=test", args);
        AssertSecureBaseline(args);
        Assert.True(imageIndex >= 0);
        Assert.Equal("--entrypoint", args[imageIndex - 2]);
        Assert.Equal("sh", args[imageIndex - 1]);
        Assert.Equal("-lc", args[imageIndex + 1]);
    }

    [Fact]
    public void CreateShellStepStartInfo_AttachesJobNetwork()
    {
        var request = new StepExecutionRequest(
            "test",
            "Run tests",
            "ubuntu-latest",
            "dotnet test",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Runtime: new JobRuntimeContext("actio-test-network", ["actio-postgres"]));

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "ubuntu:24.04", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var networkIndex = Array.IndexOf(args, "--network");

        Assert.True(networkIndex >= 0);
        Assert.Equal("actio-test-network", args[networkIndex + 1]);
    }

    [Fact]
    public void CreateDockerActionStartInfo_AttachesJobNetwork()
    {
        var request = new DockerActionExecutionRequest(
            "test",
            "Use image",
            "alpine:3.20",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Runtime: new JobRuntimeContext("actio-test-network", ["actio-postgres"]));

        var startInfo = DockerRunnerProvider.CreateDockerActionStartInfo(request, "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var networkIndex = Array.IndexOf(args, "--network");

        Assert.True(networkIndex >= 0);
        Assert.Equal("actio-test-network", args[networkIndex + 1]);
    }

    [Fact]
    public void CreateJavaScriptActionStartInfo_AttachesJobNetwork()
    {
        var actionPath = Path.Combine(Directory.GetCurrentDirectory(), "action");
        Directory.CreateDirectory(actionPath);
        var request = new JavaScriptActionExecutionRequest(
            "test",
            "Use JavaScript",
            Directory.GetCurrentDirectory(),
            actionPath,
            "dist/index.js",
            new Dictionary<string, string>(),
            Runtime: new JobRuntimeContext("actio-test-network", []));

        var startInfo = DockerRunnerProvider.CreateJavaScriptActionStartInfo(
            request,
            "dist/index.js",
            "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var networkIndex = Array.IndexOf(args, "--network");

        Assert.True(networkIndex >= 0);
        Assert.Equal("actio-test-network", args[networkIndex + 1]);
    }

    [Fact]
    public void CreateNetworkCreateStartInfo_UsesScopedOutboundBridgeWithLoopbackDefault()
    {
        var startInfo = DockerRunnerProvider.CreateNetworkCreateStartInfo("test", "actio-test-network");
        var args = startInfo.ArgumentList.ToArray();

        Assert.Contains("--driver", args);
        Assert.Contains("bridge", args);
        Assert.Contains("com.docker.network.bridge.host_binding_ipv4=127.0.0.1", args);
        Assert.DoesNotContain("--internal", args);
        Assert.Equal("actio-test-network", args[^1]);
    }

    [Theory]
    [InlineData(8080, null, "tcp", "127.0.0.1::8080/tcp")]
    [InlineData(53, 5353, "udp", "127.0.0.1:5353:53/udp")]
    public void FormatPublishedPort_AlwaysUsesIpv4Loopback(
        int containerPort,
        int? hostPort,
        string protocol,
        string expected)
    {
        Assert.Equal(
            expected,
            DockerRunnerProvider.FormatPublishedPort(new ContainerPortMapping(containerPort, hostPort, protocol)));
    }

    [Fact]
    public void CreateServiceContainerStartInfo_UsesServiceConfiguration()
    {
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "db");
        Directory.CreateDirectory(dbPath);
        var service = new ServiceContainerDefinition(
            "postgres",
            "postgres:16",
            new Dictionary<string, string>
            {
                ["POSTGRES_PASSWORD"] = "postgres"
            },
            [new ContainerPortMapping(5432, 5432)],
            ["--health-cmd=pg_isready", "--health-interval=5s"],
            [new StepExecutionMount(dbPath, "/var/lib/postgresql/data", ReadOnly: false)]);
        var request = new JobRuntimeStartRequest(
            "test",
            Directory.GetCurrentDirectory(),
            [],
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
        Assert.Contains("127.0.0.1:5432:5432/tcp", args);
        Assert.Contains("--health-cmd=pg_isready", args);
        Assert.Contains("--health-interval=5s", args);
        Assert.Contains($"type=bind,src={Path.GetFullPath(dbPath)},dst=/var/lib/postgresql/data", args);
        Assert.Contains("POSTGRES_PASSWORD=postgres", args);
        AssertSecureBaseline(args);
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

    [Fact]
    public void CreateShellStepStartInfo_ConfiguresPowerShellCore()
    {
        var request = new StepExecutionRequest(
            "test",
            "Run tests",
            "ubuntu-latest",
            "dotnet test",
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(),
            Shell: "pwsh");

        var startInfo = DockerRunnerProvider.CreateShellStepStartInfo(request, "mcr.microsoft.com/powershell:7.5-ubuntu-24.04", "actio-test");
        var args = startInfo.ArgumentList.ToArray();
        var imageIndex = Array.IndexOf(args, "mcr.microsoft.com/powershell:7.5-ubuntu-24.04");

        Assert.True(imageIndex >= 0);
        Assert.Equal("--entrypoint", args[imageIndex - 2]);
        Assert.Equal("pwsh", args[imageIndex - 1]);
        Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"], args[(imageIndex + 1)..(imageIndex + 5)]);
        Assert.Contains("$ErrorActionPreference = 'Stop'", args[imageIndex + 5]);
        Assert.Contains("$PSNativeCommandUseErrorActionPreference = $true", args[imageIndex + 5]);
        Assert.Contains("dotnet test", args[imageIndex + 5]);
        Assert.Contains("exit $LASTEXITCODE", args[imageIndex + 5]);
    }

    [Fact]
    public void CreateDockerfileActionBuildStartInfo_DoesNotRequestUnsafeEntitlements()
    {
        var actionRoot = Path.Combine(Directory.GetCurrentDirectory(), "cached-action");
        var request = new DockerfileActionExecutionRequest(
            "test",
            "Use Dockerfile action",
            "actio/action:abc123",
            Directory.GetCurrentDirectory(),
            actionRoot,
            Path.Combine(actionRoot, "Dockerfile"),
            new Dictionary<string, string>());

        var args = DockerRunnerProvider.CreateDockerfileActionBuildStartInfo(request).ArgumentList.ToArray();

        Assert.DoesNotContain("--allow", args);
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain("--ssh", args);
        Assert.DoesNotContain(args, argument => argument.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DockerfileBuildContextPreparer_ExcludesProtectedAndVcsFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"actio-build-context-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(Path.Combine(source, ".actio"));
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        File.WriteAllText(Path.Combine(source, "Dockerfile"), "FROM alpine:3.20");
        File.WriteAllText(Path.Combine(source, "app.txt"), "safe");
        File.WriteAllText(Path.Combine(source, ".dockerignore"), "*.tmp");
        File.WriteAllText(Path.Combine(source, ".actio", "secrets.env"), "TOKEN=secret");
        File.WriteAllText(Path.Combine(source, ".actio", "vars.env"), "MODE=test");
        File.WriteAllText(Path.Combine(source, ".git", "config"), "metadata");

        try
        {
            var request = new DockerfileActionExecutionRequest(
                "test",
                "Dockerfile action",
                "actio/action:test",
                root,
                source,
                Path.Combine(source, "Dockerfile"),
                new Dictionary<string, string>(),
                BuildContextStagingRoot: staging);

            var result = DockerfileBuildContextPreparer.Prepare(request);

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(result.Request!.BuildContext, "Dockerfile")));
            Assert.True(File.Exists(Path.Combine(result.Request.BuildContext, "app.txt")));
            Assert.True(File.Exists(Path.Combine(result.Request.BuildContext, ".dockerignore")));
            Assert.False(File.Exists(Path.Combine(result.Request.BuildContext, ".actio", "secrets.env")));
            Assert.False(File.Exists(Path.Combine(result.Request.BuildContext, ".actio", "vars.env")));
            Assert.False(Directory.Exists(Path.Combine(result.Request.BuildContext, ".git")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("--cap-add=SYS_ADMIN")]
    [InlineData("--device=/dev/sda")]
    [InlineData("--device-cgroup-rule=c 1:3 rmw")]
    [InlineData("--device-read-bps=/dev/sda:1mb")]
    [InlineData("--device-read-iops=/dev/sda:1000")]
    [InlineData("--device-write-bps=/dev/sda:1mb")]
    [InlineData("--device-write-iops=/dev/sda:1000")]
    [InlineData("--blkio-weight-device=/dev/sda:200")]
    [InlineData("--pid=host")]
    [InlineData("--ipc=host")]
    [InlineData("--uts=host")]
    [InlineData("--cgroupns=host")]
    [InlineData("--userns=host")]
    [InlineData("--network=host")]
    [InlineData("--network-alias=database")]
    [InlineData("--link=database")]
    [InlineData("--ip=172.20.0.2")]
    [InlineData("--publish=8080:80")]
    [InlineData("-p")]
    [InlineData("--publish-all")]
    [InlineData("-P")]
    [InlineData("--expose=80")]
    [InlineData("--security-opt=seccomp=unconfined")]
    [InlineData("--log-driver=none")]
    [InlineData("--memory-swappiness=100")]
    [InlineData("--cpu-quota=100000")]
    [InlineData("--mount")]
    [InlineData("--volume")]
    [InlineData("-v")]
    [InlineData("--volumes-from=another-container")]
    [InlineData("--volume-driver=local")]
    [InlineData("--use-api-socket")]
    [InlineData("--net=host")]
    [InlineData("--runtime=runc")]
    [InlineData("--gpus=all")]
    public void SecurityPolicy_RejectsPrivilegeAndConfinementOptions(string option)
    {
        var error = DockerRuntimeSecurityPolicy.Validate([option], [], "test container");

        Assert.NotNull(error);
        Assert.Contains("secure-baseline", error, StringComparison.Ordinal);
        Assert.Contains(option.Split('=')[0], error, StringComparison.Ordinal);
    }

    [Fact]
    public void PortLeaseManager_BlocksParallelFixedPortReuseAndReleasesLease()
    {
        var manager = new DockerPortLeaseManager();
        var port = new ContainerPortMapping(80, 8080);

        Assert.True(manager.TryAcquire("first", [port], out var firstError), firstError);
        Assert.False(manager.TryAcquire("second", [port], out var conflict));
        Assert.Contains("job 'first' already reserved it", conflict, StringComparison.Ordinal);

        manager.Release("first", [port]);

        Assert.True(manager.TryAcquire("second", [port], out var secondError), secondError);
    }

    [Fact]
    public void PortLeaseManager_DoesNotReleaseLeaseOwnedByAnotherJob()
    {
        var manager = new DockerPortLeaseManager();
        var port = new ContainerPortMapping(80, 8080);

        Assert.True(manager.TryAcquire("first", [port], out var firstError), firstError);

        manager.Release("stale", [port]);

        Assert.False(manager.TryAcquire("second", [port], out var conflict));
        Assert.Contains("job 'first' already reserved it", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void PortLeaseManager_TreatsTcpAndUdpAsSeparateBindings()
    {
        var manager = new DockerPortLeaseManager();

        Assert.True(manager.TryAcquire("tcp", [new ContainerPortMapping(80, 8080)], out var tcpError), tcpError);
        Assert.True(
            manager.TryAcquire(
                "udp",
                [new ContainerPortMapping(80, 8080, ContainerPortMapping.UdpProtocol)],
                out var udpError),
            udpError);
    }

    [Fact]
    public void PortLeaseManager_RejectsDuplicatePortsWithinJobWithoutLeakingLeases()
    {
        var manager = new DockerPortLeaseManager();
        var port = new ContainerPortMapping(80, 8080);

        Assert.False(manager.TryAcquire("duplicate", [port, port], out var duplicateError));
        Assert.Contains("duplicate fixed loopback port", duplicateError, StringComparison.Ordinal);
        Assert.True(manager.TryAcquire("next", [port], out var nextError), nextError);
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/user/1000/podman.sock")]
    [InlineData("/run/containerd/containerd.sock")]
    [InlineData("\\\\.\\pipe\\docker_engine")]
    public void SecurityPolicy_RejectsContainerRuntimeSocketMounts(string hostPath)
    {
        var error = DockerRuntimeSecurityPolicy.Validate(
            [],
            [new StepExecutionMount(hostPath, "/runtime.sock", ReadOnly: false)],
            "test container");

        Assert.NotNull(error);
        Assert.Contains("secure-baseline", error, StringComparison.Ordinal);
        Assert.Contains("socket", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecurityMetadata_ReportsAppliedBaselineAndUnknownDaemonState()
    {
        var metadata = new DockerRunnerProvider().SecurityMetadata;

        Assert.Equal("docker", metadata.Provider);
        Assert.Equal("secure-baseline", metadata.RequestedProfile);
        Assert.Equal("secure-baseline", metadata.EffectiveProfile);
        Assert.Contains("no-new-privileges=true", metadata.AppliedSecurityOptions);
        Assert.Equal("docker-default-no-additions", metadata.CapabilityPolicy);
        Assert.Equal("not-evaluated", metadata.DaemonPlatformState);
        Assert.Contains("daemon-platform-security-not-evaluated", metadata.DegradedControls);
        Assert.Equal("image-default-user-with-root-warning", metadata.UserPolicy);
        Assert.Equal("writable", metadata.RootFilesystemPolicy);
        Assert.Equal("read-write-with-protected-value-file-masks", metadata.WorkspacePolicy);
        Assert.Contains("/workspace/.actio/secrets.env", metadata.ProtectedPaths);
        Assert.Equal("per-job-user-defined-bridge-with-outbound", metadata.NetworkPolicy);
        Assert.Equal("ipv4-loopback-only", metadata.PublishedPortPolicy);
    }

    [Fact]
    public void GetOwnerState_RecognizesActiveOwnerAndPidReuse()
    {
        using var process = Process.GetCurrentProcess();
        var start = process.StartTime.ToUniversalTime().Ticks;

        Assert.Equal(
            DockerResourceOwnerState.Active,
            DockerCleanupPolicy.GetOwnerState(process.Id, start));
        Assert.Equal(
            DockerResourceOwnerState.Stale,
            DockerCleanupPolicy.GetOwnerState(process.Id, start - 1));
    }

    [Fact]
    public void GetOwnerState_TreatsMissingProcessAsStale()
    {
        Assert.Equal(
            DockerResourceOwnerState.Stale,
            DockerCleanupPolicy.GetOwnerState(int.MaxValue, 1));
    }

    [Fact]
    public void CreateNetworkObservation_RecordsServicesAndLoopbackPortPolicy()
    {
        var request = new JobRuntimeStartRequest(
            "test",
            Directory.GetCurrentDirectory(),
            [new ContainerPortMapping(8080)],
            [
                new ServiceContainerDefinition(
                    "postgres",
                    "postgres:16",
                    new Dictionary<string, string>(),
                    [new ContainerPortMapping(5432, 15432)])
            ]);

        var observation = DockerRunnerProvider.CreateNetworkObservation(request, "actio-test-network");

        Assert.Equal("test", observation.JobName);
        Assert.Equal("user-defined-bridge", observation.Mode);
        Assert.True(observation.OutboundAllowed);
        Assert.False(observation.Internal);
        Assert.Equal(["postgres"], observation.ServiceAliases);
        Assert.Collection(
            observation.PublishedPorts,
            port =>
            {
                Assert.Equal("job-container", port.Surface);
                Assert.Equal("127.0.0.1", port.BindAddress);
                Assert.Equal("dynamic", port.Assignment);
            },
            port =>
            {
                Assert.Equal("service:postgres", port.Surface);
                Assert.Equal(15432, port.HostPort);
                Assert.Equal("fixed", port.Assignment);
            });
    }

    [Fact]
    public void CreateNetworkCreateStartInfo_StrictNetworkIsInternalAndOwned()
    {
        var execution = new RunnerExecutionContext(
            "run-1",
            RunnerSecurityProfiles.Strict,
            RunnerSecurityProfiles.Strict,
            ContainerResourceLimits.Defaults,
            new ActioInstanceIdentity("instance-1", 42, 1234));

        var args = DockerRunnerProvider.CreateNetworkCreateStartInfo(
                "test",
                "actio-test-network",
                execution,
                internalNetwork: true)
            .ArgumentList
            .ToArray();

        Assert.Contains("--internal", args);
        Assert.Contains("io.actio.managed=true", args);
        Assert.Contains("io.actio.instance=instance-1", args);
        Assert.Contains("io.actio.resource=network", args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(18080)]
    public void ValidatePublishedPorts_StrictRejectsDynamicAndFixedHostPorts(int? hostPort)
    {
        var execution = new RunnerExecutionContext(
            "run-1",
            RunnerSecurityProfiles.Strict,
            RunnerSecurityProfiles.Strict,
            ContainerResourceLimits.Defaults,
            new ActioInstanceIdentity("instance-1", 42, 1234));

        var error = DockerRuntimeSecurityPolicy.ValidatePublishedPorts(
            [new ContainerPortMapping(8080, hostPort)],
            execution);

        Assert.Contains("Strict profile blocks host port publication", error);
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

    private static void AssertSecureBaseline(IReadOnlyList<string> args)
    {
        var securityOptionIndex = Array.IndexOf(args.ToArray(), "--security-opt");
        Assert.True(securityOptionIndex >= 0);
        Assert.Equal("no-new-privileges=true", args[securityOptionIndex + 1]);
        Assert.DoesNotContain("--cap-add", args);
        Assert.DoesNotContain("--privileged", args);
    }

    private static void AssertOptionValue(IReadOnlyList<string> args, string option, string expected)
    {
        var index = Array.IndexOf(args.ToArray(), option);
        Assert.True(index >= 0, $"Option '{option}' was not present.");
        Assert.Equal(expected, args[index + 1]);
    }
}
