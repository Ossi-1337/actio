using System.Reflection;

namespace Actio.Runner.Docker;

internal static class JavaScriptActionRuntimeDockerfile
{
    private const string ResourceName = "Actio.Runner.Docker.RuntimeImages.JavaScriptAction.Dockerfile";

    public static string Content { get; } = ReadContent();

    private static string ReadContent()
    {
        using var stream = typeof(JavaScriptActionRuntimeDockerfile).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded runtime Dockerfile '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
