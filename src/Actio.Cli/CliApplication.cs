using Actio.Core.Workflows;
using Actio.Engine.Execution;
using Actio.Runner.Docker;

namespace Actio.Cli;

public sealed class CliApplication
{
    private readonly WorkflowFileResolver _resolver;
    private readonly WorkflowParser _parser;
    private readonly IWorkflowExecutor _executor;

    public CliApplication()
        : this(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            new WorkflowExecutor(new DockerRunnerProvider()))
    {
    }

    public CliApplication(WorkflowFileResolver resolver, WorkflowParser parser, IWorkflowExecutor executor)
    {
        _resolver = resolver;
        _parser = parser;
        _executor = executor;
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
        if (args.Length != 1)
        {
            error.WriteLine("Usage: actio <workflow>.yml");
            return ExitCodes.UsageError;
        }

        var resolution = _resolver.Resolve(args[0], workingDirectory);
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
