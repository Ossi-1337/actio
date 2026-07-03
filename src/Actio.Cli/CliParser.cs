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

        if (string.Equals(args[0], "rerun", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRunManagementCommand(
                args,
                "rerun",
                CliCommandKind.ShowRerunHelp,
                CliCommand.RerunWorkflow);
        }

        if (string.Equals(args[0], "cancel", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRunManagementCommand(
                args,
                "cancel",
                CliCommandKind.ShowCancelHelp,
                CliCommand.CancelRun);
        }

        if (string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRunManagementCommand(
                args,
                "status",
                CliCommandKind.ShowStatusHelp,
                CliCommand.ShowRunStatus);
        }

        if (string.Equals(args[0], "cache", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCacheCommand(args);
        }

        if (string.Equals(args[0], "compatibility", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCompatibilityCommand(args);
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

        if (args[1].StartsWith("-", StringComparison.Ordinal))
        {
            return CliCommand.UsageError($"Unknown option '{args[1]}' for 'run'.");
        }

        if (!IsWorkflowFilename(args[1]))
        {
            return CliCommand.UsageError("Workflow argument for 'run' must end with .yml or .yaml.");
        }

        var inputs = ParseRunOptions(args, 2, "run");
        return inputs.Success
            ? CliCommand.RunWorkflow(args[1], inputs.Inputs)
            : CliCommand.UsageError(inputs.ErrorMessage!);
    }

    private static CliCommand ParseShorthandRunCommand(IReadOnlyList<string> args)
    {
        var workflowName = args[0];

        if (args.Count == 1)
        {
            return CliCommand.RunWorkflow(workflowName);
        }

        var inputs = ParseRunOptions(args, 1, null);
        return inputs.Success
            ? CliCommand.RunWorkflow(workflowName, inputs.Inputs)
            : CliCommand.UsageError(inputs.ErrorMessage!);
    }

    private static RunOptionsParseResult ParseRunOptions(
        IReadOnlyList<string> args,
        int startIndex,
        string? commandName)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = startIndex; index < args.Count; index++)
        {
            var option = args[index];
            if (!option.StartsWith("-", StringComparison.Ordinal))
            {
                return RunOptionsParseResult.Failed(commandName is null
                    ? $"Unexpected argument '{option}'."
                    : $"Unexpected argument '{option}' for '{commandName}'.");
            }

            if (!string.Equals(option, "--input", StringComparison.Ordinal))
            {
                return RunOptionsParseResult.Failed(commandName is null
                    ? $"Unknown option '{option}'."
                    : $"Unknown option '{option}' for '{commandName}'.");
            }

            if (index + 1 >= args.Count)
            {
                return RunOptionsParseResult.Failed("Missing value for '--input'.");
            }

            var input = args[++index];
            var separatorIndex = input.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return RunOptionsParseResult.Failed("Value for '--input' must use name=value.");
            }

            var name = input[..separatorIndex];
            var value = input[(separatorIndex + 1)..];
            if (!IsInputName(name))
            {
                return RunOptionsParseResult.Failed($"Input name '{name}' is invalid.");
            }

            if (!inputs.TryAdd(name, value))
            {
                return RunOptionsParseResult.Failed($"Input '{name}' was provided more than once.");
            }
        }

        return RunOptionsParseResult.Parsed(inputs);
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

    private static CliCommand ParseRunManagementCommand(
        IReadOnlyList<string> args,
        string commandName,
        CliCommandKind helpKind,
        Func<string, CliCommand> createCommand)
    {
        if (args.Count == 2 && IsHelp(args[1]))
        {
            return new CliCommand(helpKind);
        }

        if (args.Count == 1)
        {
            return CliCommand.UsageError($"Missing run id argument for '{commandName}'.");
        }

        if (args.Count > 2)
        {
            return CliCommand.UsageError($"Unexpected argument '{args[2]}' for '{commandName}'.");
        }

        if (args[1].StartsWith("-", StringComparison.Ordinal))
        {
            return CliCommand.UsageError($"Unknown option '{args[1]}' for '{commandName}'.");
        }

        return createCommand(args[1]);
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

    private static CliCommand ParseCompatibilityCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
        {
            return CliCommand.ShowCompatibility();
        }

        if (args.Count == 2 && IsHelp(args[1]))
        {
            return new CliCommand(CliCommandKind.ShowCompatibilityHelp);
        }

        return args[1].StartsWith("-", StringComparison.Ordinal)
            ? CliCommand.UsageError($"Unknown option '{args[1]}' for 'compatibility'.")
            : CliCommand.UsageError($"Unexpected argument '{args[1]}' for 'compatibility'.");
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

    private static bool IsInputName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character == '_' ||
            character == '-');
    }

    private sealed record RunOptionsParseResult(
        bool Success,
        IReadOnlyDictionary<string, string> Inputs,
        string? ErrorMessage)
    {
        public static RunOptionsParseResult Parsed(IReadOnlyDictionary<string, string> inputs)
            => new(true, inputs, null);

        public static RunOptionsParseResult Failed(string errorMessage)
            => new(false, new Dictionary<string, string>(), errorMessage);
    }
}
