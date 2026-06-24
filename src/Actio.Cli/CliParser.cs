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

        if (string.Equals(args[0], "cache", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCacheCommand(args);
        }

        if (IsWorkflowFilename(args[0]))
        {
            return ParseShorthandRunCommand(args);
        }

        if (args.Count == 1)
        {
            return ParseSingleArgument(args[0]);
        }

        if (args[0].StartsWith("-", StringComparison.Ordinal))
        {
            return CliCommand.UsageError($"Unknown option '{args[0]}'.");
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
            return args[2].StartsWith("-", StringComparison.Ordinal)
                ? CliCommand.UsageError($"Unknown option '{args[2]}' for 'run'.")
                : CliCommand.UsageError($"Unexpected argument '{args[2]}' for 'run'.");
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

    private static CliCommand ParseShorthandRunCommand(IReadOnlyList<string> args)
    {
        var workflowName = args[0];

        if (args.Count == 1)
        {
            return CliCommand.RunWorkflow(workflowName);
        }

        return args[1].StartsWith("-", StringComparison.Ordinal)
            ? CliCommand.UsageError($"Unknown option '{args[1]}'.")
            : CliCommand.UsageError($"Unexpected argument '{args[1]}'.");
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
        var background = false;

        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (!option.StartsWith("-", StringComparison.Ordinal))
            {
                return CliCommand.UsageError($"Unexpected argument '{option}' for 'web'.");
            }

            if (string.Equals(option, "--background", StringComparison.Ordinal))
            {
                background = true;
                continue;
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

        return CliCommand.RunWeb(projectRoot, actioHome, url, background);
    }

    private static CliCommand ParseCacheCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 1 || args.Count == 2 && IsHelp(args[1]))
        {
            return new CliCommand(CliCommandKind.ShowCacheHelp);
        }

        if (args.Count > 2)
        {
            return CliCommand.UsageError($"Unexpected argument '{args[2]}' for 'cache'.");
        }

        if (args[1].StartsWith("-", StringComparison.Ordinal))
        {
            return CliCommand.UsageError($"Unknown option '{args[1]}' for 'cache'.");
        }

        return args[1] switch
        {
            "list" => CliCommand.ListCache(),
            "clean" => CliCommand.CleanCache(),
            _ => CliCommand.UsageError($"Unknown cache command '{args[1]}'.")
        };
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
