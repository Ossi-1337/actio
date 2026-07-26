namespace Actio.Core.IO;

public sealed record SafeFileTreeEntry(
    string FullPath,
    string RelativePath,
    bool IsDirectory);

public sealed class SafeFileTreeException : IOException
{
    public SafeFileTreeException(string operation, string relativePath)
        : base($"{operation} rejected filesystem link '{Normalize(relativePath)}'.")
    {
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

public static class SafeFileTree
{
    public static IReadOnlyList<SafeFileTreeEntry> Enumerate(string rootPath, string operation)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"{operation} source directory does not exist.");
        }

        RejectLink(new DirectoryInfo(root), ".", operation);

        var entries = new List<SafeFileTreeEntry>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var children = Directory
                .EnumerateFileSystemEntries(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            foreach (var child in children)
            {
                var relativePath = Path.GetRelativePath(root, child);
                var info = Directory.Exists(child)
                    ? (FileSystemInfo)new DirectoryInfo(child)
                    : new FileInfo(child);
                RejectLink(info, relativePath, operation);

                var isDirectory = info is DirectoryInfo;
                entries.Add(new SafeFileTreeEntry(
                    Path.GetFullPath(child),
                    relativePath,
                    isDirectory));
                if (isDirectory)
                {
                    pending.Push(child);
                }
            }
        }

        return entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static string ValidateExistingPath(
        string boundaryRoot,
        string path,
        string operation)
    {
        var boundary = Path.GetFullPath(boundaryRoot);
        var candidate = Path.GetFullPath(path);
        if (!IsWithin(candidate, boundary) ||
            (!File.Exists(candidate) && !Directory.Exists(candidate)))
        {
            throw new IOException($"{operation} path must exist inside its allowed root.");
        }

        var relative = Path.GetRelativePath(boundary, candidate);
        var current = boundary;
        RejectLink(new DirectoryInfo(boundary), ".", operation);

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = Directory.Exists(current)
                ? (FileSystemInfo)new DirectoryInfo(current)
                : new FileInfo(current);
            RejectLink(info, Path.GetRelativePath(boundary, current), operation);
        }

        return candidate;
    }

    private static void RejectLink(FileSystemInfo info, string relativePath, string operation)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new SafeFileTreeException(operation, relativePath);
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
