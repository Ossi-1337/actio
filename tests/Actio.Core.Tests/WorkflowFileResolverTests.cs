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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
