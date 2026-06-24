using Actio.Cli;
using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Execution;
using Actio.Engine.Runs;

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
    public void Run_PrintsWebHelp()
    {
        var result = RunWithFakeExecutor(["web", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio web - start the local web UI.", result.Output);
        Assert.Contains("--project-root", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsCacheHelp()
    {
        var result = RunWithFakeExecutor(["cache", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio cache - inspect or clean local cache entries.", result.Output);
        Assert.Contains("actio cache list", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ListsCacheEntries()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cache = new FakeActionCache(
            [
                new ActionCacheEntry(
                    "key-1",
                    "local",
                    "./.actio/actions/hello",
                    "C:\\repo\\.actio\\actions\\hello\\action.yml",
                    "hash",
                    "C:\\actio\\cache\\actions\\local\\key-1",
                    DateTimeOffset.Parse("2026-06-23T10:00:00Z"),
                    DateTimeOffset.Parse("2026-06-23T11:00:00Z"))
            ]);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, actionCache: cache).Run(["cache", "list"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("cache:", output.ToString());
        Assert.Contains("local:./.actio/actions/hello", output.ToString());
        Assert.Contains("key: key-1", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CleansCacheEntries()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cache = new FakeActionCache([], cleanCount: 2);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, actionCache: cache).Run(["cache", "clean"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal($"Removed 2 cache entries.{Environment.NewLine}", output.ToString());
        Assert.True(cache.Cleaned);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_ReturnsValidationErrorWhenCacheCannotBeListed()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cache = new FakeActionCache([], listException: new IOException("cache is locked"));
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, actionCache: cache).Run(["cache", "list"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Cache could not be listed: cache is locked", error.ToString());
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
    public void Run_PrintsViewPipelineLink()
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
        var launcher = new FakeWebServerLauncher("http://127.0.0.1:17345/runs/run-1");
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(
                WorkflowExecutionStatus.Success,
                1,
                1,
                [],
                runId: "run-1"));

        var exitCode = CreateApplication(executor, launcher).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("View pipeline: http://127.0.0.1:17345/runs/run-1", output.ToString());
        Assert.Equal("run-1", launcher.RunId);
        Assert.Equal(_root, launcher.ProjectRoot);
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
        var result = RunWithFakeExecutor(["deploy"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown command 'deploy'.", result.Error);
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
            new WorkflowExecutionResult(WorkflowExecutionStatus.Failed, 0, 1, ["step failed"], failedSteps: 1));

        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Failed (0 / 1, 1 failed)", output.ToString());
        Assert.Contains("Workflow execution failed:", error.ToString());
        Assert.Contains("step failed", error.ToString());
    }

    [Fact]
    public void Run_PrintsWorkflowErrorWhenFailureIsNotAStepFailure()
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
            new WorkflowExecutionResult(WorkflowExecutionStatus.Failed, 1, 1, ["artifact failed"]));

        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Failed (1 / 1, workflow error)", output.ToString());
    }

    [Fact]
    public void Run_PrintsSkippedStepCount()
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
            new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 2, [], skippedSteps: 1));

        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (1 / 2, 1 skipped)", output.ToString());
    }

    [Fact]
    public void Run_PrintsOutputsAndArtifactsFromExecutionResult()
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

        var artifactPath = Path.Combine(_root, "actio-home", "artifacts", "run-1", "test", "report.txt");
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(
                WorkflowExecutionStatus.Success,
                1,
                1,
                [],
                [new WorkflowRunOutput("test", "coverage", "87")],
                [new WorkflowRunArtifact("test", "report", "report.txt", artifactPath)]));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("output:", output.ToString());
        Assert.Contains(" - test.coverage=87", output.ToString());
        Assert.Contains("artifacts:", output.ToString());
        Assert.Contains($" - report: {artifactPath}", output.ToString());
    }

    private CliRunResult RunWithFakeExecutor(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));
        var exitCode = CreateApplication(executor).Run(args, _root, output, error);

        return new CliRunResult(exitCode, output.ToString(), error.ToString(), executor);
    }

    private static CliApplication CreateApplication(
        FakeWorkflowExecutor executor,
        ILocalWebServerLauncher? launcher = null,
        IActionCache? actionCache = null)
    {
        return new CliApplication(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            executor,
            webServerLauncher: launcher,
            actionCache: actionCache);
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

    private sealed class FakeWebServerLauncher : ILocalWebServerLauncher
    {
        private readonly string _url;

        public FakeWebServerLauncher(string url)
        {
            _url = url;
        }

        public string? ProjectRoot { get; private set; }

        public string? RunId { get; private set; }

        public Task<string?> EnsureStartedAsync(
            string projectRoot,
            string? runId,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            ProjectRoot = projectRoot;
            RunId = runId;
            return Task.FromResult<string?>(_url);
        }
    }

    private sealed class FakeActionCache : IActionCache
    {
        private readonly IReadOnlyList<ActionCacheEntry> _entries;
        private readonly int _cleanCount;
        private readonly Exception? _listException;
        private readonly Exception? _cleanException;

        public FakeActionCache(
            IReadOnlyList<ActionCacheEntry> entries,
            int cleanCount = 0,
            Exception? listException = null,
            Exception? cleanException = null)
        {
            _entries = entries;
            _cleanCount = cleanCount;
            _listException = listException;
            _cleanException = cleanException;
        }

        public bool Cleaned { get; private set; }

        public Task<ActionCacheEntry> GetOrAddLocalActionAsync(
            LocalActionCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ActionCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
        {
            if (_listException is not null)
            {
                throw _listException;
            }

            return Task.FromResult(_entries);
        }

        public Task<int> CleanAsync(CancellationToken cancellationToken = default)
        {
            if (_cleanException is not null)
            {
                throw _cleanException;
            }

            Cleaned = true;
            return Task.FromResult(_cleanCount);
        }
    }
}
