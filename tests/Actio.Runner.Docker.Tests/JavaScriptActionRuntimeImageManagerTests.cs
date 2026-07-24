using Actio.Core.Actions;
using Actio.Engine.Execution;

namespace Actio.Runner.Docker.Tests;

public sealed class JavaScriptActionRuntimeImageManagerTests
{
    [Theory]
    [InlineData(ActionRuntime.Node20, "20.20.2", "node:20.20.2-bookworm-slim@sha256:2cf067cfed83d5ea958367df9f966191a942351a2df77d6f0193e162b5febfc0")]
    [InlineData(ActionRuntime.Node24, "24.18.0", "node:24.18.0-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d")]
    public void Catalog_UsesPinnedRuntimeDefinitions(string runtime, string nodeVersion, string baseImage)
    {
        var descriptor = JavaScriptActionRuntimeCatalog.Resolve(runtime);

        Assert.Equal(nodeVersion, descriptor.NodeVersion);
        Assert.Equal(baseImage, descriptor.BaseImage);
        Assert.Equal("1:2.39.5-0+deb12u3", descriptor.GitVersion);
        Assert.Equal("20230311+deb12u1", descriptor.CaCertificatesVersion);
        Assert.Equal("node", descriptor.StrictUser);
        Assert.Matches($"^actio/javascript-action:{runtime}-[0-9a-f]{{12}}$", descriptor.Image);
        Assert.Matches("^[0-9a-f]{64}$", descriptor.DefinitionHash);
    }

    [Fact]
    public void Dockerfile_InstallsOnlyPinnedDirectToolsAndUsesNodeUser()
    {
        var dockerfile = JavaScriptActionRuntimeDockerfile.Content;

        Assert.Contains("git=\"${GIT_VERSION}\"", dockerfile);
        Assert.Contains("ca-certificates=\"${CA_CERTIFICATES_VERSION}\"", dockerfile);
        Assert.Contains("--no-install-recommends", dockerfile);
        Assert.Contains("rm -rf /var/lib/apt/lists/*", dockerfile);
        Assert.Contains("git config --system --add safe.directory /workspace", dockerfile);
        Assert.EndsWith("USER node\n", dockerfile);
        Assert.DoesNotContain("sudo", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe.directory *", dockerfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBuildStartInfo_UsesPinnedInputsLabelsAndEmptyContext()
    {
        var runtime = JavaScriptActionRuntimeCatalog.Resolve(ActionRuntime.Node24);
        var args = DockerJavaScriptActionRuntimeImageStore.CreateBuildStartInfo(runtime)
            .ArgumentList
            .ToArray();

        Assert.Equal("build", args[0]);
        AssertOptionValue(args, "--tag", runtime.Image);
        Assert.Contains($"BASE_IMAGE={runtime.BaseImage}", args);
        Assert.Contains($"GIT_VERSION={runtime.GitVersion}", args);
        Assert.Contains($"CA_CERTIFICATES_VERSION={runtime.CaCertificatesVersion}", args);
        Assert.Contains($"{JavaScriptActionRuntimeCatalog.DefinitionLabel}={runtime.DefinitionHash}", args);
        Assert.Equal("-", args[^1]);
        Assert.DoesNotContain("--allow", args);
        Assert.DoesNotContain("--privileged", args);
    }

    [Fact]
    public async Task EnsureAsync_ReusesMatchingImage()
    {
        var runtime = JavaScriptActionRuntimeCatalog.Resolve(ActionRuntime.Node20);
        var store = new FakeRuntimeImageStore(
            new JavaScriptActionRuntimeImageInspection(true, runtime.ExpectedLabels));
        var output = new RecordingOutputSink();

        var result = await new JavaScriptActionRuntimeImageManager(store)
            .EnsureAsync(runtime, output, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(runtime.Image, result.Image);
        Assert.Equal(0, store.BuildCount);
        Assert.Empty(output.Output);
    }

    [Fact]
    public async Task EnsureAsync_RebuildsImageWithMissingOrStaleLabels()
    {
        var runtime = JavaScriptActionRuntimeCatalog.Resolve(ActionRuntime.Node24);
        var store = new FakeRuntimeImageStore(
            new JavaScriptActionRuntimeImageInspection(
                true,
                new Dictionary<string, string>
                {
                    [JavaScriptActionRuntimeCatalog.DefinitionLabel] = "stale"
                }));
        var output = new RecordingOutputSink();

        var result = await new JavaScriptActionRuntimeImageManager(store)
            .EnsureAsync(runtime, output, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, store.BuildCount);
        Assert.Contains(output.Output, line => line.Contains(runtime.BaseImage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureAsync_CoordinatesConcurrentBuildsPerRuntime()
    {
        var runtime = JavaScriptActionRuntimeCatalog.Resolve(ActionRuntime.Node24);
        var store = new FakeRuntimeImageStore(
            new JavaScriptActionRuntimeImageInspection(false),
            buildDelay: TimeSpan.FromMilliseconds(50));
        var manager = new JavaScriptActionRuntimeImageManager(store);

        var results = await Task.WhenAll(
            manager.EnsureAsync(runtime, new RecordingOutputSink(), CancellationToken.None),
            manager.EnsureAsync(runtime, new RecordingOutputSink(), CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(1, store.BuildCount);
    }

    [Fact]
    public async Task EnsureAsync_ReturnsActionableBuildFailure()
    {
        var runtime = JavaScriptActionRuntimeCatalog.Resolve(ActionRuntime.Node20);
        var store = new FakeRuntimeImageStore(
            new JavaScriptActionRuntimeImageInspection(false),
            buildResult: new JavaScriptActionRuntimeImageBuildResult(false, "network unavailable"));

        var result = await new JavaScriptActionRuntimeImageManager(store)
            .EnsureAsync(runtime, new RecordingOutputSink(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(runtime.Runtime, result.Error);
        Assert.Contains(runtime.BaseImage, result.Error);
        Assert.Contains("network unavailable", result.Error);
    }

    [Fact]
    public async Task EnsureAsync_PropagatesBuildCancellation()
    {
        var runtime = JavaScriptActionRuntimeCatalog.Resolve(ActionRuntime.Node24);
        var store = new FakeRuntimeImageStore(
            new JavaScriptActionRuntimeImageInspection(false),
            buildDelay: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JavaScriptActionRuntimeImageManager(store)
                .EnsureAsync(runtime, new RecordingOutputSink(), cancellation.Token));

        Assert.Equal(1, store.BuildCount);
    }

    private static void AssertOptionValue(string[] args, string option, string value)
    {
        var index = Array.IndexOf(args, option);
        Assert.True(index >= 0);
        Assert.Equal(value, args[index + 1]);
    }

    private sealed class FakeRuntimeImageStore(
        JavaScriptActionRuntimeImageInspection inspection,
        JavaScriptActionRuntimeImageBuildResult? buildResult = null,
        TimeSpan? buildDelay = null) : IJavaScriptActionRuntimeImageStore
    {
        private readonly object _sync = new();
        private JavaScriptActionRuntimeImageInspection _inspection = inspection;
        private int _buildCount;

        public int BuildCount => Volatile.Read(ref _buildCount);

        public Task<JavaScriptActionRuntimeImageInspection> InspectAsync(
            string image,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult(_inspection);
            }
        }

        public async Task<JavaScriptActionRuntimeImageBuildResult> BuildAsync(
            JavaScriptActionRuntimeDescriptor runtime,
            string dockerfile,
            IStepOutputSink output,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _buildCount);
            if (buildDelay is not null)
            {
                await Task.Delay(buildDelay.Value, cancellationToken);
            }

            var result = buildResult ?? new JavaScriptActionRuntimeImageBuildResult(true);
            if (result.Success)
            {
                lock (_sync)
                {
                    _inspection = new JavaScriptActionRuntimeImageInspection(true, runtime.ExpectedLabels);
                }
            }

            return result;
        }
    }

    private sealed class RecordingOutputSink : IStepOutputSink
    {
        public List<string> Output { get; } = [];

        public List<string> Error { get; } = [];

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            Output.Add(line);
            return Task.CompletedTask;
        }

        public Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default)
        {
            Error.Add(line);
            return Task.CompletedTask;
        }
    }
}
