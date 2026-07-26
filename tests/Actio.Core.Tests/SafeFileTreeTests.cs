using Actio.Core.IO;

namespace Actio.Core.Tests;

public sealed class SafeFileTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"actio-safe-tree-{Guid.NewGuid():N}");

    public SafeFileTreeTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Enumerate_ReturnsValidatedFilesAndDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "file.txt"), "content");

        var entries = SafeFileTree.Enumerate(_root, "test walk");

        Assert.Contains(entries, entry => entry.IsDirectory && entry.RelativePath == "src");
        Assert.Contains(entries, entry => !entry.IsDirectory && entry.RelativePath == Path.Combine("src", "file.txt"));
    }

    [Fact]
    public void Enumerate_RejectsFileSystemLinksWithoutDisclosingTarget()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"actio-safe-tree-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "host-secret");
        var link = Path.Combine(_root, "linked.txt");

        try
        {
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var exception = Assert.Throws<SafeFileTreeException>(
                () => SafeFileTree.Enumerate(_root, "artifact save"));
            Assert.Contains("linked.txt", exception.Message);
            Assert.DoesNotContain(outside, exception.Message);
        }
        finally
        {
            File.Delete(outside);
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
