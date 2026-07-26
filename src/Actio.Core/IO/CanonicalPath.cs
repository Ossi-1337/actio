namespace Actio.Core.IO;

public static class CanonicalPath
{
    public static string ResolveExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory '{fullPath}' does not exist.");
        }

        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Directory '{fullPath}' has no filesystem root.");
        var current = root;
        var relativePath = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"Directory '{current}' does not exist.");
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException($"Directory link '{current}' could not be resolved.");
                if (target is not DirectoryInfo)
                {
                    throw new IOException($"Directory link '{current}' does not target a directory.");
                }

                current = Path.GetFullPath(target.FullName);
            }
        }

        return Normalize(current);
    }

    public static bool AreEquivalent(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                ResolveExistingDirectory(left),
                ResolveExistingDirectory(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        var root = Path.GetPathRoot(path);
        var normalized = Path.GetFullPath(path);
        return string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
