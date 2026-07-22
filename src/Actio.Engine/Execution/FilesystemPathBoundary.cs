namespace Actio.Engine.Execution;

public static class FilesystemPathBoundary
{
    public static string ResolveExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Path '{fullPath}' does not exist.");
        }

        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path '{fullPath}' has no filesystem root.");
        var current = root;
        var remainder = fullPath[root.Length..];

        foreach (var segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = Directory.Exists(current)
                ? (FileSystemInfo)new DirectoryInfo(current)
                : new FileInfo(current);

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidOperationException($"Reparse point '{current}' could not be resolved.");
            current = Path.GetFullPath(target.FullName);
        }

        return Path.GetFullPath(current);
    }

    public static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
