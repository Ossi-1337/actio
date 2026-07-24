using Actio.Core.Actions;
using System.Security.Cryptography;
using System.Text;

namespace Actio.Runner.Docker;

internal static class JavaScriptActionRuntimeCatalog
{
    internal const string DefinitionLabel = "io.actio.javascript-runtime.definition";
    internal const string RuntimeLabel = "io.actio.javascript-runtime.name";
    internal const string BaseImageLabel = "io.actio.javascript-runtime.base-image";
    internal const string NodeVersionLabel = "io.actio.javascript-runtime.node-version";
    internal const string GitVersionLabel = "io.actio.javascript-runtime.git-version";
    internal const string CaCertificatesVersionLabel = "io.actio.javascript-runtime.ca-certificates-version";

    private const string GitVersion = "1:2.39.5-0+deb12u3";
    private const string CaCertificatesVersion = "20230311+deb12u1";

    private static readonly IReadOnlyDictionary<string, JavaScriptActionRuntimeDescriptor> Runtimes =
        new Dictionary<string, JavaScriptActionRuntimeDescriptor>(StringComparer.Ordinal)
        {
            [ActionRuntime.Node20] = Create(
                ActionRuntime.Node20,
                "20.20.2",
                "node:20.20.2-bookworm-slim@sha256:2cf067cfed83d5ea958367df9f966191a942351a2df77d6f0193e162b5febfc0"),
            [ActionRuntime.Node24] = Create(
                ActionRuntime.Node24,
                "24.18.0",
                "node:24.18.0-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d")
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

    private static JavaScriptActionRuntimeDescriptor Create(
        string runtime,
        string nodeVersion,
        string baseImage)
    {
        var definitionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            JavaScriptActionRuntimeDockerfile.Content,
            runtime,
            nodeVersion,
            baseImage,
            GitVersion,
            CaCertificatesVersion,
            "node"))));
        return new(
            runtime,
            $"actio/javascript-action:{runtime}-{definitionHash[..12]}",
            baseImage,
            nodeVersion,
            GitVersion,
            CaCertificatesVersion,
            "node",
            definitionHash);
    }
}

internal sealed record JavaScriptActionRuntimeDescriptor(
    string Runtime,
    string Image,
    string BaseImage,
    string NodeVersion,
    string GitVersion,
    string CaCertificatesVersion,
    string StrictUser,
    string DefinitionHash)
{
    public IReadOnlyDictionary<string, string> ExpectedLabels { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JavaScriptActionRuntimeCatalog.DefinitionLabel] = DefinitionHash,
            [JavaScriptActionRuntimeCatalog.RuntimeLabel] = Runtime,
            [JavaScriptActionRuntimeCatalog.BaseImageLabel] = BaseImage,
            [JavaScriptActionRuntimeCatalog.NodeVersionLabel] = NodeVersion,
            [JavaScriptActionRuntimeCatalog.GitVersionLabel] = GitVersion,
            [JavaScriptActionRuntimeCatalog.CaCertificatesVersionLabel] = CaCertificatesVersion
        };
}
