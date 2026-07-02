using System.Text;

namespace Actio.Engine.Setup;

internal static class SetupActionCommandBuilder
{
    public static string Build(SetupAction action)
    {
        var command = new StringBuilder();
        command.AppendLine("set -eu");
        command.AppendLine($"ACTIO_SETUP_ACTION={ShellQuote(action.ActionName)}");
        command.AppendLine($"ACTIO_SETUP_TOOL={ShellQuote(GetToolName(action.Kind))}");
        command.AppendLine(BuildToolResolution(action.Kind));
        command.AppendLine("case \"$ACTIO_SETUP_DETECTED_VERSION\" in");
        command.AppendLine("  [0-9]*) ;;");
        command.AppendLine("  *)");
        command.AppendLine("    printf '%s\\n' \"$ACTIO_SETUP_ACTION: could not parse $ACTIO_SETUP_TOOL version from '$ACTIO_SETUP_RAW_VERSION'.\" >&2");
        command.AppendLine("    exit 1");
        command.AppendLine("    ;;");
        command.AppendLine("esac");
        command.AppendLine("printf '%s\\n' \"$ACTIO_SETUP_ACTION: detected $ACTIO_SETUP_TOOL $ACTIO_SETUP_DETECTED_VERSION.\"");

        if (action.Kind == SetupActionKind.Java && action.Distribution is not null)
        {
            command.AppendLine($"printf '%s\\n' \"$ACTIO_SETUP_ACTION: distribution {ShellQuote(action.Distribution)} is expected to already be present in the runner image.\"");
        }

        if (action.RequestedVersion is not null && action.VersionMatchPattern is not null)
        {
            command.AppendLine("case \"$ACTIO_SETUP_DETECTED_VERSION\" in");
            command.AppendLine($"  {action.VersionMatchPattern}) ;;");
            command.AppendLine("  *)");
            command.AppendLine($"    printf '%s\\n' \"$ACTIO_SETUP_ACTION: requested $ACTIO_SETUP_TOOL version {ShellQuote(action.RequestedVersion)} but detected $ACTIO_SETUP_DETECTED_VERSION. Use a runner image that contains the requested runtime.\" >&2");
            command.AppendLine("    exit 1");
            command.AppendLine("    ;;");
            command.AppendLine("esac");
        }

        return command.ToString();
    }

    private static string BuildToolResolution(SetupActionKind kind)
    {
        return kind switch
        {
            SetupActionKind.Node => BuildSimpleToolResolution("node", "node --version 2>&1"),
            SetupActionKind.Java => BuildSimpleToolResolution("java", "java -version 2>&1 | head -n 1"),
            SetupActionKind.Go => BuildSimpleToolResolution("go", "go version 2>&1"),
            SetupActionKind.DotNet => BuildSimpleToolResolution("dotnet", "dotnet --version 2>&1"),
            SetupActionKind.Python => BuildPythonResolution(),
            _ => throw new InvalidOperationException($"Unsupported setup action kind '{kind}'.")
        };
    }

    private static string BuildSimpleToolResolution(string executable, string versionCommand)
    {
        return $$"""
if ! command -v {{executable}} >/dev/null 2>&1; then
  printf '%s\n' "$ACTIO_SETUP_ACTION: {{executable}} is not available in this runner image. Use a runner image that contains $ACTIO_SETUP_TOOL." >&2
  exit 1
fi
ACTIO_SETUP_RAW_VERSION="$({{versionCommand}})"
ACTIO_SETUP_DETECTED_VERSION="$(printf '%s\n' "$ACTIO_SETUP_RAW_VERSION" | sed -E 's/[^0-9]*([0-9]+(\.[0-9]+){0,2}).*/\1/')"
""";
    }

    private static string BuildPythonResolution()
    {
        return """
if command -v python3 >/dev/null 2>&1; then
  ACTIO_SETUP_PYTHON=python3
elif command -v python >/dev/null 2>&1; then
  ACTIO_SETUP_PYTHON=python
else
  printf '%s\n' "$ACTIO_SETUP_ACTION: python is not available in this runner image. Use a runner image that contains python." >&2
  exit 1
fi
ACTIO_SETUP_RAW_VERSION="$($ACTIO_SETUP_PYTHON --version 2>&1)"
ACTIO_SETUP_DETECTED_VERSION="$(printf '%s\n' "$ACTIO_SETUP_RAW_VERSION" | sed -E 's/[^0-9]*([0-9]+(\.[0-9]+){0,2}).*/\1/')"
""";
    }

    private static string GetToolName(SetupActionKind kind)
    {
        return kind switch
        {
            SetupActionKind.Node => "node",
            SetupActionKind.Python => "python",
            SetupActionKind.Java => "java",
            SetupActionKind.Go => "go",
            SetupActionKind.DotNet => "dotnet",
            _ => throw new InvalidOperationException($"Unsupported setup action kind '{kind}'.")
        };
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
