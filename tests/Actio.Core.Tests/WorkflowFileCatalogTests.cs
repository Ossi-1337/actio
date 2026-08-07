using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowFileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-workflow-catalog-{Guid.NewGuid():N}");

    public WorkflowFileCatalogTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".workflows"));
        Directory.CreateDirectory(Path.Combine(_root, ".github", "workflows"));
    }

    [Fact]
    public void Discover_PrefersActioWorkflowForDuplicateFilename()
    {
        var actioPath = Path.Combine(_root, ".workflows", "ci.yml");
        File.WriteAllText(actioPath, "name: Actio");
        File.WriteAllText(Path.Combine(_root, ".github", "workflows", "ci.yml"), "name: GitHub");
        var fallbackPath = Path.Combine(_root, ".github", "workflows", "release.yaml");
        File.WriteAllText(fallbackPath, "name: Release");

        var files = WorkflowFileCatalog.Discover(_root);

        Assert.Equal(2, files.Count);
        Assert.Contains(Path.GetFullPath(actioPath), files);
        Assert.Contains(Path.GetFullPath(fallbackPath), files);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
