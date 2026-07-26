namespace Actio.Storage;

internal static class CacheEntryPathEnumerator
{
    public static IReadOnlyList<string> Enumerate(
        string rootPath,
        string entryFileName)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        return new DirectoryInfo(rootPath)
            .EnumerateDirectories()
            .Where(IsCacheEntryDirectory)
            .Select(directory => Path.Combine(directory.FullName, entryFileName))
            .Where(File.Exists)
            .ToArray();
    }

    private static bool IsCacheEntryDirectory(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.Name.Length != 64)
        {
            return false;
        }

        return directory.Name.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
