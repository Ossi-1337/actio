using Actio.Git;
using System.Diagnostics;

namespace Actio.Git.Tests;

public sealed class GitRepositoryClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-git-repository-{Guid.NewGuid():N}");

    public GitRepositoryClientTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init");
        RunGit("config", "user.email", "actio-tests@example.invalid");
        RunGit("config", "user.name", "Actio Tests");
        File.WriteAllText(Path.Combine(_root, "README.md"), "initial");
        RunGit("add", "README.md");
        RunGit("commit", "-m", "initial");
    }

    [Fact]
    public async Task InspectAndStatus_ReadRepositoryState()
    {
        var client = new GitRepositoryClient();

        var inspection = await client.InspectAsync(_root);
        var clean = await client.IsCleanAsync(_root);
        File.WriteAllText(Path.Combine(_root, "untracked.txt"), "value");
        var dirty = await client.IsCleanAsync(_root);

        Assert.True(inspection.Success);
        Assert.Equal(Path.GetFullPath(_root), inspection.Value!.ProjectRoot);
        Assert.True(clean.Value);
        Assert.False(dirty.Value);
    }

    [Fact]
    public async Task GetChangedPaths_UsesCommitRangeAndNewBranchTree()
    {
        var client = new GitRepositoryClient();
        var before = RunGit("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_root, "src.txt"), "changed");
        RunGit("add", "src.txt");
        RunGit("commit", "-m", "change");
        var after = RunGit("rev-parse", "HEAD").Trim();

        var existing = await client.GetChangedPathsAsync(
            _root,
            new GitPushRefUpdate("refs/heads/main", after, "refs/heads/main", before));
        var created = await client.GetChangedPathsAsync(
            _root,
            new GitPushRefUpdate("refs/heads/main", after, "refs/heads/new", new string('0', 40)));

        Assert.Equal(["src.txt"], existing.Value);
        Assert.Equal(["README.md", "src.txt"], created.Value);
    }

    private string RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
