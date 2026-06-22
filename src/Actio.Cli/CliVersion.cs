using System.Reflection;

namespace Actio.Cli;

public static class CliVersion
{
    public static string GetVersion()
    {
        var assembly = typeof(CliVersion).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "0.1.0"
            : informationalVersion;

        var metadataSeparator = version.IndexOf('+', StringComparison.Ordinal);
        return metadataSeparator >= 0 ? version[..metadataSeparator] : version;
    }
}
