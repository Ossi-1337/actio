using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowFileResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-tests-{Guid.NewGuid():N}");

    public WorkflowFileResolverTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".workflows"));
    }

    [Fact]
    public void Resolve_UsesWorkflowsFolderForBareFilename()
    {
        var workflowPath = Path.Combine(_root, ".workflows", "ci.yml");
        File.WriteAllText(workflowPath, "name: CI");

        var result = new WorkflowFileResolver().Resolve("ci.yml", _root);

        Assert.True(result.Success);
        Assert.Equal(_root, result.ProjectRoot);
        Assert.Equal(workflowPath, result.WorkflowPath);
    }

    [Fact]
    public void Resolve_UsesGitHubWorkflowFolderWhenActioWorkflowIsMissing()
    {
        var workflowDirectory = Path.Combine(_root, ".github", "workflows");
        Directory.CreateDirectory(workflowDirectory);
        var workflowPath = Path.Combine(workflowDirectory, "ci.yml");
        File.WriteAllText(workflowPath, "name: CI");

        var result = new WorkflowFileResolver().Resolve("ci.yml", _root);

        Assert.True(result.Success);
        Assert.Equal(_root, result.ProjectRoot);
        Assert.Equal(workflowPath, result.WorkflowPath);
    }

    [Fact]
    public void Resolve_PrefersActioWorkflowFolderWhenBothRootsContainSameFilename()
    {
        var actioWorkflowPath = Path.Combine(_root, ".workflows", "ci.yml");
        File.WriteAllText(actioWorkflowPath, "name: Actio CI");
        var gitHubWorkflowDirectory = Path.Combine(_root, ".github", "workflows");
        Directory.CreateDirectory(gitHubWorkflowDirectory);
        File.WriteAllText(Path.Combine(gitHubWorkflowDirectory, "ci.yml"), "name: GitHub CI");

        var result = new WorkflowFileResolver().Resolve("ci.yml", _root);

        Assert.True(result.Success);
        Assert.Equal(actioWorkflowPath, result.WorkflowPath);
    }

    [Fact]
    public void Resolve_WalksUpToProjectRoot()
    {
        var nested = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(nested);
        var workflowPath = Path.Combine(_root, ".workflows", "ci.yml");
        File.WriteAllText(workflowPath, "name: CI");

        var result = new WorkflowFileResolver().Resolve("ci.yml", nested);

        Assert.True(result.Success);
        Assert.Equal(_root, result.ProjectRoot);
        Assert.Equal(workflowPath, result.WorkflowPath);
    }

    [Fact]
    public void Resolve_WalksUpToGitHubWorkflowProjectRoot()
    {
        Directory.Delete(Path.Combine(_root, ".git"));
        Directory.Delete(Path.Combine(_root, ".workflows"));
        var workflowDirectory = Path.Combine(_root, ".github", "workflows");
        var nested = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(workflowDirectory);
        Directory.CreateDirectory(nested);
        var workflowPath = Path.Combine(workflowDirectory, "ci.yml");
        File.WriteAllText(workflowPath, "name: CI");

        var result = new WorkflowFileResolver().Resolve("ci.yml", nested);

        Assert.True(result.Success);
        Assert.Equal(_root, result.ProjectRoot);
        Assert.Equal(workflowPath, result.WorkflowPath);
    }

    [Fact]
    public void Resolve_RejectsExplicitPathsForMilestone02()
    {
        var result = new WorkflowFileResolver().Resolve(".workflows/ci.yml", _root);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("bare workflow filenames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_ReturnsErrorWhenWorkflowDoesNotExist()
    {
        var result = new WorkflowFileResolver().Resolve("missing.yml", _root);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("was not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_RejectsWorkflowFileLinkOutsideProjectRoot()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"actio-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        var outsideWorkflow = Path.Combine(outsideDirectory, "outside.yml");
        File.WriteAllText(outsideWorkflow, "name: Outside");
        var workflowLink = Path.Combine(_root, ".workflows", "ci.yml");

        try
        {
            File.CreateSymbolicLink(workflowLink, outsideWorkflow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Directory.Delete(outsideDirectory, recursive: true);
            return;
        }

        try
        {
            var result = new WorkflowFileResolver().Resolve("ci.yml", _root);

            Assert.False(result.Success);
            Assert.Contains(
                result.Errors,
                error => error.Contains("must resolve inside the project root", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
