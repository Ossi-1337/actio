using Actio.Core.Workflows;
using Actio.Engine.Configuration;
using Actio.Engine.Execution;
using Actio.Git;

namespace Actio.Cli.Tests;

public sealed class CliHooksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-cli-hooks-{Guid.NewGuid():N}");

    public CliHooksTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".workflows"));
    }

    [Theory]
    [InlineData("install", CliCommandKind.InstallHooks)]
    [InlineData("status", CliCommandKind.ShowHooksStatus)]
    [InlineData("uninstall", CliCommandKind.UninstallHooks)]
    public void Parser_RecognizesHookCommands(string action, CliCommandKind expected)
    {
        var command = new CliParser().Parse(["hooks", action]);

        Assert.Equal(expected, command.Kind);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parser_RecognizesHooksHelp(string option)
    {
        var command = new CliParser().Parse(["hooks", option]);

        Assert.Equal(CliCommandKind.ShowHooksHelp, command.Kind);
    }

    [Fact]
    public async Task RunPrePush_UsesDestinationBranchAndDoesNotStartWeb()
    {
        WriteWorkflow("main");
        var after = new string('2', 40);
        var repository = new FakeGitRepository(_root, after, clean: true, ["src/Actio.Core/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Success);
        var launcher = new RecordingWebLauncher();
        var application = CreateApplication(executor, repository, launcher);
        using var input = new StringReader(
            $"refs/heads/dev {after} refs/heads/main {new string('1', 40)}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", "origin", "https://credential@example.invalid/repo"],
            _root,
            input,
            output,
            error);

        Assert.True(exitCode == ExitCodes.Success, error.ToString());
        Assert.NotNull(executor.Options);
        Assert.Equal("push", executor.Options.RunTrigger.EventName);
        Assert.Equal("main", executor.Options.RunTrigger.EventPayload.Properties["ref_name"]);
        Assert.Equal("origin", executor.Options.RunTrigger.EventPayload.Properties["remote"]);
        Assert.Equal(new string('1', 40), executor.Options.RunTrigger.EventPayload.Properties["diff_base"]);
        Assert.Equal("false", executor.Options.RunTrigger.EventPayload.Properties["new_ref"]);
        Assert.Equal("Git pre-push", executor.Options.RunTrigger.Source);
        Assert.Equal(RunnerSecurityProfiles.SecureBaseline, executor.Options.RunnerPolicy.RequestedProfile);
        Assert.False(launcher.Started);
        Assert.DoesNotContain("credential", output.ToString());
        Assert.DoesNotContain("credential", error.ToString());
    }

    [Fact]
    public async Task RunPrePush_DoesNotPersistDirectRemoteUrl()
    {
        WriteWorkflow("main");
        var after = new string('2', 40);
        var repository = new FakeGitRepository(_root, after, clean: true, ["src/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Success);
        var application = CreateApplication(executor, repository, new RecordingWebLauncher());
        const string remoteUrl = "https://credential@example.invalid/repo";
        using var input = new StringReader(
            $"refs/heads/main {after} refs/heads/main {new string('0', 40)}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", remoteUrl, remoteUrl],
            _root,
            input,
            output,
            error);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("direct", executor.Options!.RunTrigger.EventPayload.Properties["remote"]);
        Assert.Equal("HEAD", executor.Options.RunTrigger.EventPayload.Properties["diff_base"]);
        Assert.Equal("true", executor.Options.RunTrigger.EventPayload.Properties["new_ref"]);
        Assert.DoesNotContain("credential", output.ToString());
        Assert.DoesNotContain("credential", error.ToString());
    }

    [Fact]
    public async Task RunPrePush_SkipsWorkflowWhenDestinationBranchDoesNotMatch()
    {
        WriteWorkflow("main");
        var after = new string('2', 40);
        var repository = new FakeGitRepository(_root, after, clean: true, ["src/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Success);
        var application = CreateApplication(executor, repository, new RecordingWebLauncher());
        using var input = new StringReader(
            $"refs/heads/dev {after} refs/heads/dev {new string('1', 40)}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", "origin", "ignored"],
            _root,
            input,
            output,
            error);

        Assert.True(exitCode == ExitCodes.Success, error.ToString());
        Assert.Null(executor.Options);
        Assert.False(repository.CleanChecked);
        Assert.Contains("No push-triggered workflows matched.", output.ToString());
    }

    [Fact]
    public async Task RunPrePush_BlocksDirtyWorktreeBeforeExecution()
    {
        WriteWorkflow("main");
        var after = new string('2', 40);
        var repository = new FakeGitRepository(_root, after, clean: false, ["src/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Success);
        var application = CreateApplication(executor, repository, new RecordingWebLauncher());
        using var input = new StringReader(
            $"refs/heads/main {after} refs/heads/main {new string('1', 40)}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", "origin", "ignored"],
            _root,
            input,
            output,
            error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Null(executor.Options);
        Assert.Contains("clean worktree", error.ToString());
    }

    [Fact]
    public async Task RunPrePush_BlocksNonHeadObject()
    {
        WriteWorkflow("main");
        var repository = new FakeGitRepository(
            _root,
            new string('3', 40),
            clean: true,
            ["src/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Success);
        var application = CreateApplication(executor, repository, new RecordingWebLauncher());
        using var input = new StringReader(
            $"refs/heads/main {new string('2', 40)} refs/heads/main {new string('1', 40)}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", "origin", "ignored"],
            _root,
            input,
            output,
            error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Null(executor.Options);
        Assert.Contains("current HEAD", error.ToString());
    }

    [Fact]
    public async Task RunPrePush_RejectsMalformedGitInput()
    {
        WriteWorkflow("main");
        var repository = new FakeGitRepository(
            _root,
            new string('2', 40),
            clean: true,
            ["src/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Success);
        var application = CreateApplication(executor, repository, new RecordingWebLauncher());
        using var input = new StringReader("malformed input");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", "origin", "ignored"],
            _root,
            input,
            output,
            error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Null(executor.Options);
        Assert.Contains("Pre-push input line", error.ToString());
    }

    [Fact]
    public async Task RunPrePush_ContinuesAfterFailureAndReturnsFailure()
    {
        WriteWorkflow("main", "ci.yml");
        WriteWorkflow("main", "second.yml");
        var after = new string('2', 40);
        var repository = new FakeGitRepository(_root, after, clean: true, ["src/App.cs"]);
        var executor = new RecordingExecutor(WorkflowExecutionStatus.Failed);
        var application = CreateApplication(executor, repository, new RecordingWebLauncher());
        using var input = new StringReader(
            $"refs/heads/main {after} refs/heads/main {new string('1', 40)}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.RunAsync(
            ["hooks", "run", "pre-push", "origin", "ignored"],
            _root,
            input,
            output,
            error);

        Assert.Equal(ExitCodes.ValidationError, exitCode);
        Assert.Equal(2, executor.ExecutionCount);
    }

    private CliApplication CreateApplication(
        RecordingExecutor executor,
        FakeGitRepository repository,
        RecordingWebLauncher launcher)
        => new(
            new WorkflowFileResolver(),
            new WorkflowParser(),
            executor,
            webServerLauncher: launcher,
            createRunId: () => $"run-{executor.ExecutionCount + 1}",
            configurationProvider: new FakeConfigurationProvider(),
            gitHookManager: new FakeHookManager(),
            gitRepository: repository);

    private void WriteWorkflow(string branch, string fileName = "ci.yml")
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", fileName),
            $$"""
            name: {{Path.GetFileNameWithoutExtension(fileName)}}
            on:
              push:
                branches:
                  - {{branch}}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: echo test
            """);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingExecutor(WorkflowExecutionStatus status) : IWorkflowExecutor
    {
        public WorkflowExecutionOptions? Options { get; private set; }

        public int ExecutionCount { get; private set; }

        public Task<WorkflowExecutionResult> ExecuteAsync(
            WorkflowDocument workflow,
            WorkflowExecutionOptions options,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            ExecutionCount++;
            var result = status == WorkflowExecutionStatus.Success
                ? new WorkflowExecutionResult(status, 1, 1, [])
                : new WorkflowExecutionResult(status, 0, 1, ["failed"]);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingWebLauncher : ILocalWebServerLauncher
    {
        public bool Started { get; private set; }

        public Task<string?> EnsureStartedAsync(
            string projectRoot,
            string? runId,
            TextWriter error,
            CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.FromResult<string?>("http://127.0.0.1:1234");
        }
    }

    private sealed class FakeGitRepository(
        string root,
        string head,
        bool clean,
        IReadOnlyList<string> changedPaths) : IGitRepositoryClient
    {
        public bool CleanChecked { get; private set; }

        public Task<GitOperationResult<GitRepositoryInfo>> InspectAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GitOperationResult<GitRepositoryInfo>.Succeeded(
                new GitRepositoryInfo(root, Path.Combine(root, ".git"), Path.Combine(root, ".git"), null)));

        public Task<GitOperationResult<bool>> IsCleanAsync(
            string projectRoot,
            CancellationToken cancellationToken = default)
        {
            CleanChecked = true;
            return Task.FromResult(GitOperationResult<bool>.Succeeded(clean));
        }

        public Task<GitOperationResult<string>> GetHeadAsync(
            string projectRoot,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GitOperationResult<string>.Succeeded(head));

        public Task<GitOperationResult<IReadOnlyList<string>>> GetChangedPathsAsync(
            string projectRoot,
            GitPushRefUpdate update,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GitOperationResult<IReadOnlyList<string>>.Succeeded(changedPaths));
    }

    private sealed class FakeHookManager : IGitHookManager
    {
        public Task<GitHookResult> InstallAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHookResult(true, GitHookState.Managed, "installed"));

        public Task<GitHookResult> GetStatusAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHookResult(true, GitHookState.Managed, "installed"));

        public Task<GitHookResult> UninstallAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHookResult(true, GitHookState.NotInstalled, "removed"));
    }

    private sealed class FakeConfigurationProvider : IActioConfigurationProvider
    {
        public ActioConfigurationLoadResult Load()
            => new(
                true,
                new ContainerResourceConfiguration(),
                new ActioInstanceIdentity("test", Environment.ProcessId, 1));

        public ActioConfigurationValidationResult Validate()
            => new(true, new ContainerResourceConfiguration());
    }
}
