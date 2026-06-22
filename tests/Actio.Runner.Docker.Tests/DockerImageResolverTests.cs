namespace Actio.Runner.Docker.Tests;

public sealed class DockerImageResolverTests
{
    [Fact]
    public void TryResolveImage_MapsUbuntuLatest()
    {
        var resolved = new DockerImageResolver().TryResolveImage("ubuntu-latest", out var image);

        Assert.True(resolved);
        Assert.Equal("ubuntu:24.04", image);
    }

    [Fact]
    public void SupportsRunner_ReturnsFalseForUnknownLabel()
    {
        var provider = new DockerRunnerProvider();

        Assert.False(provider.SupportsRunner("windows-latest"));
    }
}
