namespace Actio.Cli;

public sealed class CliApplication
{
    private readonly WorkflowFileResolver _resolver;
    private readonly WorkflowParser _parser;

    public CliApplication()
        : this(new WorkflowFileResolver(), new WorkflowParser())
    {
    }

    public CliApplication(WorkflowFileResolver resolver, WorkflowParser parser)
    {
        _resolver = resolver;
        _parser = parser;
    }

    public int Run(string[] args, string workingDirectory, TextWriter output, TextWriter error)
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
        output.WriteLine($"Workflow '{workflow.Name}' is valid.");
        output.WriteLine($"Jobs: {workflow.Jobs.Count}");
        output.WriteLine($"Steps: {workflow.StepCount}");
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
}
