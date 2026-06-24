using Actio.Core.Actions;

namespace Actio.Core.Tests;

public sealed class ActionReferenceTests
{
    [Fact]
    public void TryParse_ClassifiesLocalReference()
    {
        var success = ActionReference.TryParse("./.actio/actions/test", out var reference);

        Assert.True(success);
        Assert.Equal(ActionReferenceKind.Local, reference!.Kind);
        Assert.False(reference.IsRemote);
        Assert.True(reference.IsPinned);
    }

    [Fact]
    public void TryParse_ClassifiesDockerTagAsMutableRemoteReference()
    {
        var success = ActionReference.TryParse("docker://node:22", out var reference);

        Assert.True(success);
        Assert.Equal(ActionReferenceKind.DockerImage, reference!.Kind);
        Assert.True(reference.IsRemote);
        Assert.True(reference.IsMutable);
        Assert.Equal("22", reference.MutablePart);
    }

    [Fact]
    public void TryParse_ClassifiesDockerDigestAsPinnedRemoteReference()
    {
        var digest = new string('a', 64);
        var success = ActionReference.TryParse($"docker://node@sha256:{digest}", out var reference);

        Assert.True(success);
        Assert.Equal(ActionReferenceKind.DockerImage, reference!.Kind);
        Assert.True(reference.IsRemote);
        Assert.True(reference.IsPinned);
        Assert.False(reference.IsMutable);
    }

    [Fact]
    public void TryParse_ClassifiesGitHubTagAsMutableRemoteReference()
    {
        var success = ActionReference.TryParse("actions/checkout@v4", out var reference);

        Assert.True(success);
        Assert.Equal(ActionReferenceKind.GitHubRepository, reference!.Kind);
        Assert.True(reference.IsRemote);
        Assert.True(reference.IsMutable);
        Assert.Equal("v4", reference.MutablePart);
    }

    [Fact]
    public void TryParse_ClassifiesGitHubCommitShaAsPinnedRemoteReference()
    {
        var sha = new string('b', 40);
        var success = ActionReference.TryParse($"owner/repo/path@{sha}", out var reference);

        Assert.True(success);
        Assert.Equal(ActionReferenceKind.GitHubRepository, reference!.Kind);
        Assert.True(reference.IsRemote);
        Assert.True(reference.IsPinned);
        Assert.False(reference.IsMutable);
    }

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("docker://")]
    [InlineData("docker://node@sha256:not-a-valid-digest")]
    [InlineData("owner/../action@v1")]
    [InlineData("not-a-reference")]
    public void TryParse_RejectsUnsupportedReferences(string value)
    {
        var success = ActionReference.TryParse(value, out var reference);

        Assert.False(success);
        Assert.Null(reference);
    }
}
