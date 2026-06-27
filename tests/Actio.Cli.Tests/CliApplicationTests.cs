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
        Assert.Contains(".github/workflows fallback", result.Output);
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
        Assert.Contains("--input NAME=VALUE", result.Output);
        Assert.Contains(".github/workflows/<workflow>.yml", result.Output);
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
                    "github",
                    "owner/repo/action@v1",
                    "C:\\actio\\cache\\actions\\github\\key-1\\source\\action\\action.yml",
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "C:\\actio\\cache\\actions\\github\\key-1",
                    DateTimeOffset.Parse("2026-06-23T10:00:00Z"),
                    DateTimeOffset.Parse("2026-06-23T11:00:00Z"),
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "v1")
            ]);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, actionCache: cache).Run(["cache", "list"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("cache:", output.ToString());
        Assert.Contains("github:owner/repo/action@v1", output.ToString());
        Assert.Contains("key: key-1", output.ToString());
        Assert.Contains("pinned: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", output.ToString());
        Assert.Contains("mutable: v1", output.ToString());
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
        Assert.Equal("run-1", executor.Options!.RunId);
        Assert.Equal("workflow_dispatch", executor.Options.RunTrigger.EventName);
        Assert.Equal("CLI", executor.Options.RunTrigger.Source);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_PassesWorkflowDispatchInputsToExecution()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            name: CI
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    required: true
                    type: choice
                    options:
                      - staging
                      - production
                  dry-run:
                    type: boolean
                    default: "false"
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        var result = RunWithFakeExecutor(["run", "ci.yml", "--input", "environment=staging"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("staging", result.Executor.Options!.RunTrigger.Inputs["environment"]);
        Assert.Equal("false", result.Executor.Options.RunTrigger.Inputs["dry-run"]);
    }

    [Fact]
    public void Run_ReturnsValidationErrorForMissingRequiredWorkflowDispatchInput()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            name: CI
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    required: true
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        var result = RunWithFakeExecutor(["ci.yml"]);

        Assert.Equal(ExitCodes.ValidationError, result.ExitCode);
        Assert.Contains("workflow_dispatch input 'environment' is required.", result.Error);
        Assert.Null(result.Executor.Options);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForInvalidWorkflowDispatchInputOption()
    {
        var result = RunWithFakeExecutor(["ci.yml", "--input", "environment"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Value for '--input' must use name=value.", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ColorizesSuccessWhenTerminalSupportsAnsi()
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
        var formatter = CreateOutputFormatter(output, redirected: false);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, outputFormatter: formatter).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("\u001b[32mSuccess\u001b[0m (1 / 1)", output.ToString());
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
        Assert.Equal("run-1", result.Executor.Options!.RunId);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void Run_AcceptsExternalUsesAndPrintsMutableWarning()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Node action
                    uses: docker://node:22
            """);

        var result = RunWithFakeExecutor(["ci.yml"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Workflow warnings:", result.Error);
        Assert.Contains("mutable Docker image reference", result.Error);
        Assert.Equal("docker://node:22", result.Executor.Workflow!.Jobs["test"].Steps[0].Uses);
    }

    [Fact]
    public void Run_PrintsWarningForTopLevelCompatibilityField()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            """
            name: CI
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        var result = RunWithFakeExecutor(["ci.yml"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Workflow warnings:", result.Error);
        Assert.Contains("workflow.on is parsed as trigger metadata", result.Error);
        Assert.NotNull(result.Executor.Workflow);
    }

    [Fact]
    public void Run_AcceptsExternalUsesFromOfficialRunCommand()
    {
        var sha = new string('b', 40);
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "ci.yml"),
            $$"""
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Pinned action
                    uses: owner/repo/action@{{sha}}
            """);

        var result = RunWithFakeExecutor(["run", "ci.yml"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal($"owner/repo/action@{sha}", result.Executor.Workflow!.Jobs["test"].Steps[0].Uses);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForRemovedRemoteActionsFlag()
    {
        var result = RunWithFakeExecutor(["run", "--allow-remote-actions"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown option '--allow-remote-actions' for 'run'.", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForUnknownShorthandOption()
    {
        var result = RunWithFakeExecutor(["ci.yml", "--unknown"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("Unknown option '--unknown'.", result.Error);
        Assert.Null(result.Executor.Workflow);
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

        var exitCode = CreateApplication(executor, launcher, createRunId: () => "run-1").Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("View pipeline: http://127.0.0.1:17345/runs/run-1", output.ToString());
        Assert.Contains($"View pipeline: http://127.0.0.1:17345/runs/run-1{Environment.NewLine}{Environment.NewLine}Success (1 / 1)", output.ToString());
        Assert.Equal("run-1", launcher.RunId);
        Assert.Equal(_root, launcher.ProjectRoot);
        Assert.Equal("run-1", executor.Options!.RunId);
    }

    [Fact]
    public void Run_PrintsViewPipelineLinkBeforeExecutionOutput()
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
        var launcher = new FakeWebServerLauncher("http://127.0.0.1:17345/runs/run-early");
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, [], runId: "run-early"),
            outputLine: "[test] Test");

        var exitCode = CreateApplication(executor, launcher, createRunId: () => "run-early").Run(["ci.yml"], _root, output, error);

        var text = output.ToString();
        var linkIndex = text.IndexOf("View pipeline:", StringComparison.Ordinal);
        var executionOutputIndex = text.IndexOf("[test] Test", StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(linkIndex >= 0);
        Assert.True(executionOutputIndex >= 0);
        Assert.True(linkIndex < executionOutputIndex);
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
    public void Run_ColorizesFailureWhenTerminalSupportsAnsi()
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
        var formatter = CreateOutputFormatter(output, redirected: false);
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(WorkflowExecutionStatus.Failed, 0, 1, ["step failed"], failedSteps: 1));

        var exitCode = CreateApplication(executor, outputFormatter: formatter).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("\u001b[31mFailed\u001b[0m (0 / 1, 1 failed)", output.ToString());
    }

    [Fact]
    public void Run_DoesNotColorizeStatusWhenOutputIsRedirected()
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
        var formatter = CreateOutputFormatter(output, redirected: true);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, outputFormatter: formatter).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (1 / 1)", output.ToString());
        Assert.DoesNotContain("\u001b[", output.ToString());
    }

    [Fact]
    public void Run_DoesNotColorizeStatusWhenNoColorIsSet()
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
        var formatter = CreateOutputFormatter(output, redirected: false, noColor: string.Empty);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, outputFormatter: formatter).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (1 / 1)", output.ToString());
        Assert.DoesNotContain("\u001b[", output.ToString());
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
        Assert.Contains($"Success (1 / 1){Environment.NewLine}{Environment.NewLine}output:", output.ToString());
        Assert.Contains(" - test.coverage=87", output.ToString());
        Assert.Contains("artifacts:", output.ToString());
        Assert.Contains($" - test.coverage=87{Environment.NewLine}{Environment.NewLine}artifacts:", output.ToString());
        Assert.Contains($" - report: {artifactPath}", output.ToString());
    }

    [Fact]
    public void Run_PrintsClickableArtifactPathWhenTerminalSupportsLinks()
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
                artifacts: [new WorkflowRunArtifact("test", "report", "report.txt", artifactPath)]));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var formatter = CreateOutputFormatter(output, redirected: false);

        var exitCode = CreateApplication(executor, outputFormatter: formatter).Run(["ci.yml"], _root, output, error);

        var expectedUri = new Uri(Path.GetFullPath(artifactPath)).AbsoluteUri;
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains($"\u001b]8;;{expectedUri}\u0007{artifactPath}\u001b]8;;\u0007", output.ToString());
    }

    [Fact]
    public void Run_PrintsPlainArtifactPathWhenOutputIsRedirected()
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
                artifacts: [new WorkflowRunArtifact("test", "report", "report.txt", artifactPath)]));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var formatter = CreateOutputFormatter(output, redirected: true);

        var exitCode = CreateApplication(executor, outputFormatter: formatter).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains($" - report: {artifactPath}", output.ToString());
        Assert.DoesNotContain("\u001b]8;;", output.ToString());
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
        IActionCache? actionCache = null,
        CliOutputFormatter? outputFormatter = null,
        Func<string>? createRunId = null)
    {
        return new CliApplication(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            executor,
            webServerLauncher: launcher ?? new FakeWebServerLauncher(null),
            actionCache: actionCache,
            outputFormatter: outputFormatter,
            createRunId: createRunId ?? (() => "run-1"));
    }

    private static CliOutputFormatter CreateOutputFormatter(
        TextWriter consoleOutput,
        bool redirected,
        string? noColor = null)
    {
        return new CliOutputFormatter(
            name => string.Equals(name, "NO_COLOR", StringComparison.Ordinal) ? noColor : null,
            () => redirected,
            () => consoleOutput);
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
        private readonly string? _outputLine;

        public FakeWorkflowExecutor(WorkflowExecutionResult result, string? outputLine = null)
        {
            _result = result;
            _outputLine = outputLine;
        }

        public WorkflowDocument? Workflow { get; private set; }

        public WorkflowExecutionOptions? Options { get; private set; }

        public Task<WorkflowExecutionResult> ExecuteAsync(
            WorkflowDocument workflow,
            WorkflowExecutionOptions options,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            Workflow = workflow;
            Options = options;

            if (_outputLine is not null)
            {
                output.WriteLine(_outputLine);
            }

            return Task.FromResult(_result);
        }
    }

    private sealed class FakeWebServerLauncher : ILocalWebServerLauncher
    {
        private readonly string? _url;

        public FakeWebServerLauncher(string? url)
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

        public Task<ActionCacheEntry> GetOrAddDockerImageActionAsync(
            DockerImageActionCacheRequest request,
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
