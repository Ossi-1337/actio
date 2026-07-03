namespace Actio.Cli;

public sealed class CliOutputFormatter
{
    private const string Reset = "\u001b[0m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Yellow = "\u001b[33m";

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<bool> _isOutputRedirected;
    private readonly Func<TextWriter> _getConsoleOutput;

    public CliOutputFormatter()
        : this(Environment.GetEnvironmentVariable, () => Console.IsOutputRedirected, () => Console.Out)
    {
    }

    public CliOutputFormatter(
        Func<string, string?> getEnvironmentVariable,
        Func<bool> isOutputRedirected,
        Func<TextWriter> getConsoleOutput)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _isOutputRedirected = isOutputRedirected;
        _getConsoleOutput = getConsoleOutput;
    }

    public string FormatStatus(string status, TextWriter output)
    {
        if (!SupportsTerminalFormatting(output))
        {
            return status;
        }

        return status switch
        {
            "Success" => $"{Green}{status}{Reset}",
            "Failed" => $"{Red}{status}{Reset}",
            "Cancelled" => $"{Yellow}{status}{Reset}",
            _ => status
        };
    }

    public string FormatFilePath(string path, TextWriter output)
    {
        if (!SupportsTerminalFormatting(output))
        {
            return path;
        }

        try
        {
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            return $"\u001b]8;;{uri}\u0007{path}\u001b]8;;\u0007";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return path;
        }
    }

    private bool SupportsTerminalFormatting(TextWriter output)
    {
        return ReferenceEquals(output, _getConsoleOutput())
            && !_isOutputRedirected()
            && _getEnvironmentVariable("NO_COLOR") is null;
    }
}
