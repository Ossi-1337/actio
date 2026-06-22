using Actio.Core.Workflows;
using Actio.Engine.Execution;
using Actio.Runner.Docker;

namespace Actio.Cli;

public sealed class CliApplication
{
    private readonly WorkflowFileResolver _resolver;
    private readonly WorkflowParser _parser;
    private readonly IWorkflowExecutor _executor;
    private readonly CliParser _cliParser;

    public CliApplication()
        : this(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            new WorkflowExecutor(new DockerRunnerProvider()),
            new CliParser())
    {
    }

    public CliApplication(
        WorkflowFileResolver resolver,
        WorkflowParser parser,
        IWorkflowExecutor executor,
        CliParser? cliParser = null)
    {
        _resolver = resolver;
        _parser = parser;
        _executor = executor;
        _cliParser = cliParser ?? new CliParser();
    }

    public int Run(string[] args, string workingDirectory, TextWriter output, TextWriter error)
    {
        return RunAsync(args, workingDirectory, output, error).GetAwaiter().GetResult();
    }

    public async Task<int> RunAsync(
        string[] args,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var command = _cliParser.Parse(args);

        switch (command.Kind)
        {
            case CliCommandKind.ShowRootHelp:
                output.WriteLine(CliHelpText.Root);
                return ExitCodes.Success;
            case CliCommandKind.ShowRunHelp:
                output.WriteLine(CliHelpText.Run);
                return ExitCodes.Success;
            case CliCommandKind.ShowVersion:
                output.WriteLine($"actio {CliVersion.GetVersion()}");
                return ExitCodes.Success;
            case CliCommandKind.UsageError:
                WriteUsageError(error, command.ErrorMessage!);
                return ExitCodes.UsageError;
            case CliCommandKind.RunWorkflow:
                return await RunWorkflowAsync(command.WorkflowName!, workingDirectory, output, error, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported CLI command kind '{command.Kind}'.");
        }
    }

    private async Task<int> RunWorkflowAsync(
        string workflowName,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var resolution = _resolver.Resolve(workflowName, workingDirectory);
        if (!resolution.Success)
        {
            WriteErrors(error, resolution.Errors);
            return ExitCodes.ValidationError;
        }

        var parseResult = _parser.ParseFile(resolution.WorkflowPath!);
        if (!parseResult.Success)
        {
            WriteErrors(error, parseResult.Errors);
            return ExitCodes.ValidationError;
        }

        var workflow = parseResult.Workflow!;
        var executionResult = await _executor.ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(resolution.ProjectRoot!),
            output,
            error,
            cancellationToken);

        if (!executionResult.Success)
        {
            WriteExecutionErrors(error, executionResult.Errors);
            output.WriteLine($"Failed ({executionResult.SuccessfulSteps} / {executionResult.TotalSteps})");
            return ExitCodes.ValidationError;
        }

        output.WriteLine($"Success ({executionResult.SuccessfulSteps} / {executionResult.TotalSteps})");
        return ExitCodes.Success;
    }

    private static void WriteUsageError(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("Run 'actio --help' for usage.");
    }

    private static void WriteErrors(TextWriter error, IReadOnlyList<string> errors)
    {
        error.WriteLine("Workflow validation failed:");

        foreach (var item in errors)
        {
            error.WriteLine($" - {item}");
        }
    }

    private static void WriteExecutionErrors(TextWriter error, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        error.WriteLine("Workflow execution failed:");

        foreach (var item in errors)
        {
            error.WriteLine($" - {item}");
        }
    }
}
