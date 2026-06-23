using System.Reflection;

namespace Actio.Web;

internal static class EmbeddedWebAssetLoader
{
    private static readonly Assembly Assembly = typeof(EmbeddedWebAssetLoader).Assembly;

    public static string ReadText(string relativePath)
    {
        var resourceName = FindResourceName(relativePath);
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded web asset '{relativePath}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FindResourceName(string relativePath)
    {
        var suffix = ".wwwroot." + relativePath.Replace('/', '.').Replace('\\', '.');
        return Assembly.GetManifestResourceNames()
            .First(name => name.EndsWith(suffix, StringComparison.Ordinal));
    }
}
