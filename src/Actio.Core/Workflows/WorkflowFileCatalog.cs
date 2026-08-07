namespace Actio.Core.Workflows;

public static class WorkflowFileCatalog
{
    public static IReadOnlyList<string> Discover(string projectRoot)
    {
        var primaryDirectory = Path.Combine(projectRoot, ".workflows");
        var fallbackDirectory = Path.Combine(projectRoot, ".github", "workflows");
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var files = new Dictionary<string, string>(comparer);

        AddDirectory(files, primaryDirectory);
        AddDirectory(files, fallbackDirectory);

        return files.Values
            .Order(comparer)
            .ToArray();
    }

    private static void AddDirectory(Dictionary<string, string> files, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory)
                     .Where(IsWorkflowFile)
                     .Order(StringComparer.Ordinal))
        {
            files.TryAdd(Path.GetFileName(path), Path.GetFullPath(path));
        }
    }

    private static bool IsWorkflowFile(string path)
        => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
}
