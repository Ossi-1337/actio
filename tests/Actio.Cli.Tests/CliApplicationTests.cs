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
    public void Run_PrintsRootHelpForLongHelpOption()
    {
        var result = RunWithFakeExecutor(["--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio - local-first workflow runner.", result.Output);
        Assert.Contains("Usage:", result.Output);
        Assert.Contains("Commands:", result.Output);
        Assert.Contains("Options:", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsRootHelpForShortHelpOption()
    {
        var result = RunWithFakeExecutor(["-h"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("actio run <workflow>.yml", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsRunHelpForLongHelpOption()
    {
        var result = RunWithFakeExecutor(["run", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio run - run a workflow.", result.Output);
        Assert.Contains("Arguments:", result.Output);
        Assert.Contains("Description:", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsRunHelpForShortHelpOption()
    {
        var result = RunWithFakeExecutor(["run", "-h"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("actio run <workflow>.yml", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsVersion()
    {
        var result = RunWithFakeExecutor(["--version"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal($"actio 0.1.0{Environment.NewLine}", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ExecutesOfficialRunCommand()
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
        var exitCode = CreateApplication(executor).Run(["run", "ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (1 / 1)", output.ToString());
        Assert.Equal("CI", executor.Workflow!.Name);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_ExecutesWorkflowShorthand()
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

        var result = RunWithFakeExecutor(["ci.yml"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Success (1 / 1)", result.Output);
        Assert.Equal("CI", result.Executor.Workflow!.Name);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void Run_ReturnsUsageErrorWithoutArguments()
    {
        var result = RunWithFakeExecutor([]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Missing command or workflow.", result.Error);
        Assert.Contains("actio --help", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForRunWithoutWorkflowArgument()
    {
        var result = RunWithFakeExecutor(["run"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Missing workflow argument for 'run'.", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForUnknownRootOption()
    {
        var result = RunWithFakeExecutor(["--unknown"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown option '--unknown'.", result.Error);
        Assert.DoesNotContain("Workflow validation failed:", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForUnknownRunOption()
    {
        var result = RunWithFakeExecutor(["run", "--unknown"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown option '--unknown' for 'run'.", result.Error);
        Assert.DoesNotContain("Workflow validation failed:", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForUnknownCommand()
    {
        var result = RunWithFakeExecutor(["cache"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown command 'cache'.", result.Error);
        Assert.Null(result.Executor.Workflow);
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
        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

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

        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Failed (0 / 1)", output.ToString());
        Assert.Contains("Workflow execution failed:", error.ToString());
        Assert.Contains("step failed", error.ToString());
    }

    private CliRunResult RunWithFakeExecutor(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));
        var exitCode = CreateApplication(executor).Run(args, _root, output, error);

        return new CliRunResult(exitCode, output.ToString(), error.ToString(), executor);
    }

    private static CliApplication CreateApplication(FakeWorkflowExecutor executor)
    {
        return new CliApplication(new WorkflowFileResolver(), new WorkflowParser(), executor);
    }

    private sealed record CliRunResult(
        int ExitCode,
        string Output,
        string Error,
        FakeWorkflowExecutor Executor);

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
