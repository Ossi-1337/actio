using Actio.Core.Actions;

namespace Actio.Runner.Docker;

internal static class JavaScriptActionRuntimeCatalog
{
    private static readonly IReadOnlyDictionary<string, JavaScriptActionRuntimeDescriptor> Runtimes =
        new Dictionary<string, JavaScriptActionRuntimeDescriptor>(StringComparer.Ordinal)
        {
            [ActionRuntime.Node20] = new(ActionRuntime.Node20, "node:20-bookworm-slim", "node"),
            [ActionRuntime.Node24] = new(ActionRuntime.Node24, "node:24-bookworm-slim", "node")
        };

    public static IReadOnlyList<string> SupportedRuntimes { get; } =
        Runtimes.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public static bool TryResolve(string runtime, out JavaScriptActionRuntimeDescriptor descriptor)
    {
        return Runtimes.TryGetValue(runtime, out descriptor!);
    }

    public static JavaScriptActionRuntimeDescriptor Resolve(string runtime)
    {
        if (TryResolve(runtime, out var descriptor))
        {
            return descriptor;
        }

        throw new ArgumentException(
            $"JavaScript action runtime '{runtime}' is unsupported. Supported runtimes: {string.Join(", ", SupportedRuntimes)}.",
            nameof(runtime));
    }
}

internal sealed record JavaScriptActionRuntimeDescriptor(
    string Runtime,
    string Image,
    string StrictUser);
