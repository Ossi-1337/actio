using System.Security.Cryptography;
using System.Text;
using Actio.Core.IO;

namespace Actio.Cli;

internal sealed record WebProjectSession(
    string Id,
    string ProjectRoot,
    string ActioHome)
{
    public static WebProjectSession Create(string projectRoot, string actioHome)
    {
        Directory.CreateDirectory(actioHome);
        var canonicalProjectRoot = CanonicalPath.ResolveExistingDirectory(projectRoot);
        var canonicalActioHome = CanonicalPath.ResolveExistingDirectory(actioHome);
        var identityProjectRoot = NormalizeForIdentity(canonicalProjectRoot);
        var identityActioHome = NormalizeForIdentity(canonicalActioHome);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identityProjectRoot}\n{identityActioHome}"));

        return new WebProjectSession(
            Convert.ToHexString(hash).ToLowerInvariant()[..24],
            canonicalProjectRoot,
            canonicalActioHome);
    }

    private static string NormalizeForIdentity(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}
