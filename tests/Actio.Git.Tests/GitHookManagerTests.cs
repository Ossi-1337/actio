using Actio.Git;

namespace Actio.Git.Tests;

public sealed class GitHookManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-git-hooks-{Guid.NewGuid():N}");

    public GitHookManagerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
    }

    [Fact]
    public async Task Install_IsIdempotent_AndUninstallRemovesManagedHook()
    {
        var manager = CreateManager();

        var first = await manager.InstallAsync(_root);
        var second = await manager.InstallAsync(_root);
        var status = await manager.GetStatusAsync(_root);
        var uninstall = await manager.UninstallAsync(_root);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(GitHookState.Managed, status.State);
        Assert.True(uninstall.Success);
        Assert.False(File.Exists(Path.Combine(_root, ".git", "hooks", "pre-push")));
    }

    [Fact]
    public async Task Install_DoesNotOverwriteUnmanagedHook()
    {
        var hookPath = Path.Combine(_root, ".git", "hooks", "pre-push");
        await File.WriteAllTextAsync(hookPath, "#!/bin/sh\necho custom\n");

        var result = await CreateManager().InstallAsync(_root);

        Assert.False(result.Success);
        Assert.Equal("#!/bin/sh\necho custom\n", await File.ReadAllTextAsync(hookPath));
    }

    [Fact]
    public async Task Uninstall_DoesNotRemoveUnmanagedHook()
    {
        var hookPath = Path.Combine(_root, ".git", "hooks", "pre-push");
        await File.WriteAllTextAsync(hookPath, "#!/bin/sh\necho custom\n");

        var result = await CreateManager().UninstallAsync(_root);

        Assert.False(result.Success);
        Assert.True(File.Exists(hookPath));
    }

    [Fact]
    public async Task Install_RequiresActioOnPath()
    {
        var manager = CreateManager(actioAvailable: false);

        var result = await manager.InstallAsync(_root);

        Assert.False(result.Success);
        Assert.Contains("PATH", result.Message);
    }

    [Theory]
    [InlineData(true, null, "Linked Git worktrees")]
    [InlineData(false, ".githooks", "core.hooksPath")]
    public async Task Install_RejectsUnsupportedRepositoryLayouts(
        bool linkedWorktree,
        string? hooksPath,
        string expectedMessage)
    {
        var repository = new FakeRepository(_root, linkedWorktree, hooksPath);
        var manager = new GitHookManager(repository, new FakeActioProbe(true));

        var result = await manager.InstallAsync(_root);

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.Message);
    }

    private GitHookManager CreateManager(bool actioAvailable = true)
        => new(new FakeRepository(_root), new FakeActioProbe(actioAvailable));

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeActioProbe(bool available) : IActioExecutableProbe
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(available);
    }

    private sealed class FakeRepository(
        string root,
        bool linkedWorktree = false,
        string? hooksPath = null) : IGitRepositoryClient
    {
        public Task<GitOperationResult<GitRepositoryInfo>> InspectAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            var gitDirectory = Path.Combine(root, ".git");
            var commonDirectory = linkedWorktree
                ? Path.Combine(root, ".git", "common")
                : gitDirectory;
            return Task.FromResult(GitOperationResult<GitRepositoryInfo>.Succeeded(
                new GitRepositoryInfo(root, gitDirectory, commonDirectory, hooksPath)));
        }

        public Task<GitOperationResult<bool>> IsCleanAsync(
            string projectRoot,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitOperationResult<string>> GetHeadAsync(
            string projectRoot,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GitOperationResult<IReadOnlyList<string>>> GetChangedPathsAsync(
            string projectRoot,
            GitPushRefUpdate update,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
