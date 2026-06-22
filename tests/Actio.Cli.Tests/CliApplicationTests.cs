using Actio.Cli;
using Actio.Core.Workflows;
using Actio.Engine.Execution;

namespace Actio.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-cli-tests-{Guid.NewGuid():N}");

    public CliApplicationTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".workflows"));
    }

    [Fact]
    public void Run_PrintsValidationSummaryForValidWorkflow()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        using var output = new StringWriter();
        using var error = new StringWriter();

        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));
        var exitCode = new CliApplication(new WorkflowFileResolver(), new WorkflowParser(), executor)
            .Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (1 / 1)", output.ToString());
        Assert.Equal("CI", executor.Workflow!.Name);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_ReturnsUsageErrorWithoutWorkflowArgument()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 0, 0, []));
        var exitCode = new CliApplication(new WorkflowFileResolver(), new WorkflowParser(), executor)
            .Run([], _root, output, error);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("Usage: actio <workflow>.yml", error.ToString());
        Assert.Null(executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsValidationErrorForInvalidWorkflow()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            jobs: []
            """);

        using var output = new StringWriter();
        using var error = new StringWriter();

        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 0, 0, []));
        var exitCode = new CliApplication(new WorkflowFileResolver(), new WorkflowParser(), executor)
            .Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Workflow validation failed:", error.ToString());
        Assert.Contains("workflow.name is required.", error.ToString());
        Assert.Null(executor.Workflow);
    }

    [Fact]
    public void Run_PrintsFailureSummaryForFailedExecution()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        using var output = new StringWriter();
        using var error = new StringWriter();
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(WorkflowExecutionStatus.Failed, 0, 1, ["step failed"]));

        var exitCode = new CliApplication(new WorkflowFileResolver(), new WorkflowParser(), executor)
            .Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Failed (0 / 1)", output.ToString());
        Assert.Contains("Workflow execution failed:", error.ToString());
        Assert.Contains("step failed", error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeWorkflowExecutor : IWorkflowExecutor
    {
        private readonly WorkflowExecutionResult _result;

        public FakeWorkflowExecutor(WorkflowExecutionResult result)
        {
            _result = result;
        }

        public WorkflowDocument? Workflow { get; private set; }

        public Task<WorkflowExecutionResult> ExecuteAsync(
            WorkflowDocument workflow,
            WorkflowExecutionOptions options,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            Workflow = workflow;
            return Task.FromResult(_result);
        }
    }
}
