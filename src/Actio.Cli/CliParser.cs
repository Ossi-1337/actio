namespace Actio.Cli;

public sealed class CliParser
{
    public CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return CliCommand.UsageError("Missing command or workflow.");
        }

        if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRunCommand(args);
        }

        if (string.Equals(args[0], "web", StringComparison.OrdinalIgnoreCase))
        {
            return ParseWebCommand(args);
        }

        if (args.Count == 1)
        {
            return ParseSingleArgument(args[0]);
        }

        return CliCommand.UsageError($"Unknown command '{args[0]}'.");
    }

    private static CliCommand ParseSingleArgument(string arg)
    {
        if (IsHelp(arg))
        {
            return new CliCommand(CliCommandKind.ShowRootHelp);
        }

        if (string.Equals(arg, "--version", StringComparison.Ordinal))
        {
            return new CliCommand(CliCommandKind.ShowVersion);
        }

        if (arg.StartsWith("-", StringComparison.Ordinal))
        {
            return CliCommand.UsageError($"Unknown option '{arg}'.");
        }

        if (IsWorkflowFilename(arg))
        {
            return CliCommand.RunWorkflow(arg);
        }

        return CliCommand.UsageError($"Unknown command '{arg}'.");
    }

    private static CliCommand ParseRunCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && IsHelp(args[1]))
        {
            return new CliCommand(CliCommandKind.ShowRunHelp);
        }

        if (args.Count == 1)
        {
            return CliCommand.UsageError("Missing workflow argument for 'run'.");
        }

        if (args.Count > 2)
        {
            return CliCommand.UsageError($"Unexpected argument '{args[2]}' for 'run'.");
        }

        if (args[1].StartsWith("-", StringComparison.Ordinal))
        {
            return CliCommand.UsageError($"Unknown option '{args[1]}' for 'run'.");
        }

        if (!IsWorkflowFilename(args[1]))
        {
            return CliCommand.UsageError("Workflow argument for 'run' must end with .yml or .yaml.");
        }

        return CliCommand.RunWorkflow(args[1]);
    }

    private static CliCommand ParseWebCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && IsHelp(args[1]))
        {
            return new CliCommand(CliCommandKind.ShowWebHelp);
        }

        string? projectRoot = null;
        string? actioHome = null;
        string? url = null;

        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (!option.StartsWith("-", StringComparison.Ordinal))
            {
                return CliCommand.UsageError($"Unexpected argument '{option}' for 'web'.");
            }

            if (index + 1 >= args.Count)
            {
                return CliCommand.UsageError($"Missing value for '{option}'.");
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                return CliCommand.UsageError($"Value for '{option}' cannot be empty.");
            }

            switch (option)
            {
                case "--project-root":
                    projectRoot = value;
                    break;
                case "--actio-home":
                    actioHome = value;
                    break;
                case "--url":
                    url = value;
                    break;
                default:
                    return CliCommand.UsageError($"Unknown option '{option}' for 'web'.");
            }
        }

        return CliCommand.RunWeb(projectRoot, actioHome, url);
    }

    private static bool IsHelp(string arg)
    {
        return string.Equals(arg, "--help", StringComparison.Ordinal) ||
            string.Equals(arg, "-h", StringComparison.Ordinal);
    }

    private static bool IsWorkflowFilename(string arg)
    {
        return arg.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            arg.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    }
}
