using Actio.Cli;
using Actio.Core.Security;
using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Caching;
using Actio.Engine.Execution;
using Actio.Engine.Runs;
using Actio.Storage;

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
        Assert.Contains("actio compatibility", result.Output);
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
    public void Run_PrintsCompatibilityHelp()
    {
        var result = RunWithFakeExecutor(["compatibility", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio compatibility - show known action compatibility.", result.Output);
        Assert.Contains("actio compatibility", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsCompatibilityMatrix()
    {
        var result = RunWithFakeExecutor(["compatibility"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio action compatibility matrix", result.Output);
        Assert.Contains("actions/checkout", result.Output);
        Assert.Contains("actions/github-script", result.Output);
        Assert.Contains("dorny/paths-filter", result.Output);
        Assert.Contains("Details:", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_ReturnsUsageErrorForUnexpectedCompatibilityArgument()
    {
        var result = RunWithFakeExecutor(["compatibility", "actions/checkout"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("Unexpected argument 'actions/checkout' for 'compatibility'.", result.Error);
        Assert.Contains("actio --help", result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsRerunHelp()
    {
        var result = RunWithFakeExecutor(["rerun", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio rerun - rerun a completed workflow run.", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsCancelHelp()
    {
        var result = RunWithFakeExecutor(["cancel", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio cancel - request cancellation for a running workflow run.", result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Executor.Workflow);
    }

    [Fact]
    public void Run_PrintsStatusHelp()
    {
        var result = RunWithFakeExecutor(["status", "--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Actio status - show stored workflow run status.", result.Output);
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
        var dependencyCache = new FakeDependencyCache(
            [
                new DependencyCacheEntry(
                    "nuget-main",
                    "version-1",
                    [".nuget/packages"],
                    "C:\\actio\\cache\\dependencies\\key-1",
                    DateTimeOffset.Parse("2026-06-23T10:00:00Z"),
                    DateTimeOffset.Parse("2026-06-23T11:00:00Z"))
            ]);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, actionCache: cache, dependencyCache: dependencyCache).Run(["cache", "list"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("cache:", output.ToString());
        Assert.Contains("action:", output.ToString());
        Assert.Contains("github:owner/repo/action@v1", output.ToString());
        Assert.Contains("key: key-1", output.ToString());
        Assert.Contains("pinned: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", output.ToString());
        Assert.Contains("mutable: v1", output.ToString());
        Assert.Contains("dependency:", output.ToString());
        Assert.Contains("nuget-main", output.ToString());
        Assert.Contains("paths: .nuget/packages", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CleansCacheEntries()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cache = new FakeActionCache([], cleanCount: 2);
        var dependencyCache = new FakeDependencyCache([], cleanCount: 3);
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var exitCode = CreateApplication(executor, actionCache: cache, dependencyCache: dependencyCache).Run(["cache", "clean"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal($"Removed 5 cache entries.{Environment.NewLine}", output.ToString());
        Assert.True(cache.Cleaned);
        Assert.True(dependencyCache.Cleaned);
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
    public async Task Run_PrintsStoredRunStatus()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var runStore = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await SaveRunAsync(runStore, CreateRun("run-status", "CI", workflowPath, status: "Success"));
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CreateApplication(executor, runStore: runStore).Run(["status", "run-status"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Run: run-status", output.ToString());
        Assert.Contains("Workflow: CI", output.ToString());
        Assert.Contains("Status: Success", output.ToString());
        Assert.Contains("Jobs: 1", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.Null(executor.Workflow);
    }

    [Fact]
    public async Task Run_RequestsCancellationForRunningRun()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var runStore = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await SaveRunAsync(runStore, CreateRun("run-cancel", "CI", workflowPath, status: "Running"));
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CreateApplication(executor, runStore: runStore).Run(["cancel", "run-cancel"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Cancellation requested for run run-cancel.", output.ToString());
        Assert.True(await runStore.IsRunCancellationRequestedAsync("run-cancel"));
        Assert.Equal(string.Empty, error.ToString());
        Assert.Null(executor.Workflow);
    }

    [Fact]
    public async Task Run_RerunsCompletedRunWithStoredInputs()
    {
        var workflowPath = WriteWorkflow(
            "ci.yml",
            "CI",
            """
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    required: true
            """);
        var runStore = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await SaveRunAsync(
            runStore,
            CreateRun(
                "run-source",
                "CI",
                workflowPath,
                runTrigger: new WorkflowRunTrigger(
                    "workflow_dispatch",
                    "CLI",
                    new Dictionary<string, string> { ["environment"] = "staging" })));
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CreateApplication(
            executor,
            runStore: runStore,
            createRunId: () => "run-rerun").Run(["rerun", "run-source"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (1 / 1)", output.ToString());
        Assert.Equal("CI", executor.Workflow!.Name);
        Assert.Equal("run-rerun", executor.Options!.RunId);
        Assert.Equal("rerun:run-source", executor.Options.RunTrigger.Source);
        Assert.Equal("staging", executor.Options.RunTrigger.Inputs["environment"]);
        Assert.Contains("Workflow warnings:", error.ToString());
        Assert.Contains("workflow.on is parsed as trigger metadata", error.ToString());
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
        Assert.Equal("workflow_dispatch", executor.Options.RunTrigger.EventPayload.EventName);
        Assert.Equal("CLI", executor.Options.RunTrigger.EventPayload.Source);
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
        Assert.Equal("staging", result.Executor.Options.RunTrigger.EventPayload.Inputs["environment"]);
        Assert.Equal("false", result.Executor.Options.RunTrigger.EventPayload.Inputs["dry-run"]);
    }

    [Fact]
    public void Run_LoadsLocalVarsAndSecretsIntoExecutionOptions()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".actio"));
        File.WriteAllText(Path.Combine(_root, ".actio", "vars.env"), "BUILD_CONFIGURATION=Release");
        File.WriteAllText(Path.Combine(_root, ".actio", "secrets.env"), "NUGET_TOKEN=local-secret");
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
        Assert.Equal("Release", result.Executor.Options!.Variables["BUILD_CONFIGURATION"]);
        Assert.Equal("local-secret", result.Executor.Options.Secrets["NUGET_TOKEN"]);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void Run_ReturnsValidationErrorForInvalidLocalValueFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".actio"));
        File.WriteAllText(Path.Combine(_root, ".actio", "secrets.env"), "1BAD=value");
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

        Assert.Equal(ExitCodes.ValidationError, result.ExitCode);
        Assert.Contains("Workflow validation failed:", result.Error);
        Assert.Contains("invalid secret name '1BAD'", result.Error);
        Assert.Null(result.Executor.Options);
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
    public void Run_PrintsSecurityFindings()
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
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(
            WorkflowExecutionStatus.Success,
            1,
            1,
            [],
            securityFindings:
            [
                new WorkflowSecurityFinding(
                    "warning",
                    "external-action.mutable-ref",
                    "workflow.jobs.test.steps[0].uses",
                    "External action 'docker://node:22' uses mutable identity '22'.",
                    "Pin Docker image actions with a sha256 digest for safer reuse.")
            ]));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("security:", output.ToString());
        Assert.Contains("warning: workflow.jobs.test.steps[0].uses", output.ToString());
        Assert.Contains("External action 'docker://node:22' uses mutable identity '22'.", output.ToString());
        Assert.Contains("recommendation: Pin Docker image actions with a sha256 digest for safer reuse.", output.ToString());
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
    public void Run_ReturnsValidationErrorForReusableOnlyWorkflow()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "reusable.yml"),
            """
            name: Reusable Build
            on:
              workflow_call:
                inputs:
                  configuration:
                    type: string
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        using var output = new StringWriter();
        using var error = new StringWriter();

        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 0, 0, []));
        var exitCode = CreateApplication(executor).Run(["reusable.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Workflow 'Reusable Build' is reusable through workflow_call and cannot be run directly yet.", error.ToString());
        Assert.Contains("Reusable workflow caller jobs are planned for a later milestone.", error.ToString());
        Assert.DoesNotContain("Workflow validation failed:", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
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
    public void Run_PrintsCancelledSummaryForCancelledExecution()
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
            new WorkflowExecutionResult(WorkflowExecutionStatus.Cancelled, 0, 1, ["Workflow run was cancelled."]));

        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Contains("Cancelled (0 / 1, workflow cancelled)", output.ToString());
        Assert.Contains("Workflow execution cancelled:", error.ToString());
        Assert.Contains("Workflow run was cancelled.", error.ToString());
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
    public void Run_PrintsContinuedStepCount()
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
                    continue-on-error: true
            """);

        using var output = new StringWriter();
        using var error = new StringWriter();
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 0, 1, [], continuedSteps: 1));

        var exitCode = CreateApplication(executor).Run(["ci.yml"], _root, output, error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Success (0 / 1, 1 continued)", output.ToString());
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
        IDependencyCache? dependencyCache = null,
        CliOutputFormatter? outputFormatter = null,
        FileSystemLocalValueProvider? localValueProvider = null,
        FileSystemRunStore? runStore = null,
        Func<string>? createRunId = null)
    {
        return new CliApplication(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            executor,
            webServerLauncher: launcher ?? new FakeWebServerLauncher(null),
            actionCache: actionCache,
            dependencyCache: dependencyCache,
            outputFormatter: outputFormatter,
            localValueProvider: localValueProvider,
            runStore: runStore,
            createRunId: createRunId ?? (() => "run-1"));
    }

    private string WriteWorkflow(string fileName, string name, string? extraTopLevelYaml = null)
    {
        var path = Path.Combine(_root, ".workflows", fileName);
        File.WriteAllText(
            path,
            $$"""
            name: {{name}}
            {{extraTopLevelYaml ?? string.Empty}}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);
        return path;
    }

    private async Task SaveRunAsync(FileSystemRunStore runStore, WorkflowRunRecord run)
    {
        await runStore.InitializeRunAsync(run.RunId);
        await runStore.SaveRunRecordAsync(run);
    }

    private WorkflowRunRecord CreateRun(
        string runId,
        string workflowName,
        string workflowPath,
        string status = "Success",
        WorkflowRunTrigger? runTrigger = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        return new WorkflowRunRecord(
            runId,
            workflowName,
            workflowPath,
            _root,
            status,
            startedAt,
            startedAt,
            10,
            [
                new JobRunRecord(
                    "test",
                    status,
                    "ubuntu-latest",
                    [],
                    null,
                    startedAt,
                    startedAt,
                    10,
                    new Dictionary<string, string>(),
                    [new StepRunRecord("Test", status, "dotnet test", 0, null, startedAt, startedAt, 10)],
                    [],
                    [])
            ],
            [],
            [],
            [],
            RunTrigger: runTrigger);
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

        public Task<ActionCacheEntry> GetOrAddDockerfileActionAsync(
            DockerfileActionCacheRequest request,
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

    private sealed class FakeDependencyCache : IDependencyCache
    {
        private readonly IReadOnlyList<DependencyCacheEntry> _entries;
        private readonly int _cleanCount;

        public FakeDependencyCache(
            IReadOnlyList<DependencyCacheEntry> entries,
            int cleanCount = 0)
        {
            _entries = entries;
            _cleanCount = cleanCount;
        }

        public bool Cleaned { get; private set; }

        public Task<DependencyCacheRestoreResult> RestoreAsync(
            DependencyCacheRestoreRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<DependencyCacheSaveResult> SaveAsync(
            DependencyCacheSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DependencyCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entries);
        }

        public Task<int> CleanAsync(CancellationToken cancellationToken = default)
        {
            Cleaned = true;
            return Task.FromResult(_cleanCount);
        }
    }
}
