using Actio.Engine.Execution;

namespace Actio.Engine.Tests;

public sealed class ContainerFilesystemPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-filesystem-policy-{Guid.NewGuid():N}");

    public ContainerFilesystemPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ValidateMounts_AcceptsExistingWorkspaceSource()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;

        var errors = ContainerFilesystemPolicy.ValidateMounts(
            _root,
            [new StepExecutionMount(source, "/cache", ReadOnly: false)]);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateMounts_RejectsMissingSource()
    {
        var errors = ContainerFilesystemPolicy.ValidateMounts(
            _root,
            [new StepExecutionMount(Path.Combine(_root, "missing"), "/cache", ReadOnly: false)]);

        Assert.Contains(errors, error => error.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateMounts_RejectsSourceOutsideWorkspace()
    {
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"actio-outside-{Guid.NewGuid():N}"));
        try
        {
            var errors = ContainerFilesystemPolicy.ValidateMounts(
                _root,
                [new StepExecutionMount(outside.FullName, "/cache", ReadOnly: false)]);

            Assert.Contains(errors, error => error.Contains("outside project root", StringComparison.Ordinal));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void ValidateMounts_RejectsProtectedActioDirectory()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, ".actio")).FullName;

        var errors = ContainerFilesystemPolicy.ValidateMounts(
            _root,
            [new StepExecutionMount(source, "/data", ReadOnly: true)]);

        Assert.Contains(errors, error => error.Contains("protected Actio value files", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateMounts_RejectsProjectRootAsWorkflowVolumeSource()
    {
        var errors = ContainerFilesystemPolicy.ValidateMounts(
            _root,
            [new StepExecutionMount(_root, "/data", ReadOnly: true)]);

        Assert.Contains(errors, error => error.Contains("protected Actio value files", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateMounts_RejectsReservedAndDuplicateTargets()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;

        var errors = ContainerFilesystemPolicy.ValidateMounts(
            _root,
            [
                new StepExecutionMount(first, "/workspace/cache", ReadOnly: false),
                new StepExecutionMount(second, "/workspace/cache", ReadOnly: false)
            ]);

        Assert.Contains(errors, error => error.Contains("reserved", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateMounts_AcceptsReadOnlyWorkspaceMask()
    {
        var mask = Path.Combine(_root, "empty.mask");
        File.WriteAllText(mask, string.Empty);

        var errors = ContainerFilesystemPolicy.ValidateMounts(
            _root,
            [new StepExecutionMount(
                mask,
                "/workspace/.actio/secrets.env",
                ReadOnly: true,
                StepExecutionMountKind.WorkspaceMask)]);

        Assert.Empty(errors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
