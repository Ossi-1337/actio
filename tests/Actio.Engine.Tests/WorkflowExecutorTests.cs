using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Execution;
using Actio.Engine.Runs;

namespace Actio.Engine.Tests;

public sealed class WorkflowExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RunsStepsInOrderAndReturnsSuccess()
    {
        var runner = new FakeRunnerProvider([0, 0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("Restore", "dotnet restore", null),
                    new WorkflowStep("Test", "dotnet test", null)
                ]));

        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            output,
            error);

        Assert.True(result.Success);
        Assert.Equal(2, result.SuccessfulSteps);
        Assert.Equal(2, result.TotalSteps);
        Assert.Equal(["Restore", "Test"], runner.Requests.Select(request => request.StepName));
        Assert.Equal("true", runner.Requests[0].Environment["DOTNET_NOLOGO"]);
        Assert.Contains("[test] Restore", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_SavesRunningRunRecordsBeforeFinalRecord()
    {
        var runner = new FakeRunnerProvider([0]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success);
        Assert.Equal(["Running", "Running", "Success"], store.SavedRecords.Select(record => record.Status));
        Assert.Empty(store.SavedRecords[0].Jobs);
        Assert.Equal("test", Assert.Single(store.SavedRecords[1].Jobs).Name);
    }

    [Fact]
    public async Task ExecuteAsync_StopsAfterFailedStep()
    {
        var runner = new FakeRunnerProvider([0, 42]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("One", "echo one", null),
                    new WorkflowStep("Two", "exit 42", null),
                    new WorkflowStep("Three", "echo three", null)
                ]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(3, result.TotalSteps);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains(result.Errors, error => error.Contains("exit code 42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_RunsJobsInDependencyOrder()
    {
        var runner = new FakeRunnerProvider([0, 0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                ["prepare"],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]),
            new WorkflowJob(
                "prepare",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Prepare", "dotnet restore", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success);
        Assert.Equal(["prepare", "test"], runner.Requests.Select(request => request.JobName));
        Assert.Equal(["Prepare", "Test"], runner.Requests.Select(request => request.StepName));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsDependentJobAfterFailedDependency()
    {
        var runner = new FakeRunnerProvider([42]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "prepare",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Prepare", "exit 42", null)]),
            new WorkflowJob(
                "test",
                ["prepare"],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(0, result.SuccessfulSteps);
        Assert.Equal(2, result.TotalSteps);
        Assert.Single(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("exit code 42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_EvaluatesIfConditionFromCapturedOutputs()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(0, ["actio.output changed=true"]),
                new FakeRunnerStep(0)
            ]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "prepare",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Detect changes", "echo actio.output changed=true", null)]),
            new WorkflowJob(
                "test",
                ["prepare"],
                "${{ needs.prepare.outputs.changed == 'true' }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success);
        Assert.Equal(["prepare", "test"], runner.Requests.Select(request => request.JobName));
        var output = Assert.Single(result.Outputs);
        Assert.Equal("prepare", output.JobName);
        Assert.Equal("changed", output.Name);
        Assert.Equal("true", output.Value);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsJobWhenConditionIsFalse()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(0, ["actio.output changed=false"])
            ]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "prepare",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Detect changes", "echo actio.output changed=false", null)]),
            new WorkflowJob(
                "test",
                ["prepare"],
                "${{ needs.prepare.outputs.changed == 'true' }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success);
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(2, result.TotalSteps);
        Assert.Equal(1, result.SkippedSteps);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCleanFailureWhenRunStorageInitializationFails()
    {
        var runner = new FakeRunnerProvider([0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));
        var store = new ThrowingRunStore(initializeException: new IOException("disk is unavailable"));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("initializing run storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCleanFailureWhenStepLogCannotBeOpened()
    {
        var runner = new FakeRunnerProvider([0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));
        var store = new ThrowingRunStore(openStepLogException: new IOException("log path is locked"));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("opening log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureForCircularDependency()
    {
        var runner = new FakeRunnerProvider([0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "one",
                ["two"],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("One", "echo one", null)]),
            new WorkflowJob(
                "two",
                ["one"],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Two", "echo two", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("circular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureForUnsupportedRunner()
    {
        var runner = new FakeRunnerProvider([0], supportsRunner: false);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "unknown",
                new Dictionary<string, string>(),
                [new WorkflowStep("Test", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(0, result.SuccessfulSteps);
        Assert.Equal(1, result.TotalSteps);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesAndExecutesLocalAction()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "hello"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "hello", "action.yml"),
            """
            name: Hello
            runs:
              using: composite
              steps:
                - name: First
                  run: echo first
                - name: Second
                  run: echo "actio.output greeting=hello"
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0, ["actio.output greeting=hello"])]);
            var cache = new RecordingActionCache();
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Use hello", null, "./.actio/actions/hello")]));

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal("echo first" + Environment.NewLine + "echo \"actio.output greeting=hello\"", runner.Requests[0].Command);
            Assert.Equal("./.actio/actions/hello", cache.Requests[0].Uses);
            Assert.Equal("hello", Assert.Single(result.Outputs).Value);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static WorkflowDocument CreateWorkflow(params WorkflowJob[] jobs)
    {
        return new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["DOTNET_NOLOGO"] = "true"
            },
            jobs.ToDictionary(job => job.Name, StringComparer.Ordinal));
    }

    private sealed class FakeRunnerProvider : IRunnerProvider
    {
        private readonly Queue<FakeRunnerStep> _steps;
        private readonly bool _supportsRunner;

        public FakeRunnerProvider(IEnumerable<int> exitCodes, bool supportsRunner = true)
            : this(exitCodes.Select(exitCode => new FakeRunnerStep(exitCode)), supportsRunner)
        {
        }

        public FakeRunnerProvider(IEnumerable<FakeRunnerStep> steps, bool supportsRunner = true)
        {
            _steps = new Queue<FakeRunnerStep>(steps);
            _supportsRunner = supportsRunner;
        }

        public List<StepExecutionRequest> Requests { get; } = [];

        public bool SupportsRunner(string runsOn)
        {
            return _supportsRunner;
        }

        public Task<StepExecutionResult> ExecuteStepAsync(
            StepExecutionRequest request,
            IStepOutputSink output,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ExecuteStepAsync(_steps.Dequeue(), output, cancellationToken);
        }

        private static async Task<StepExecutionResult> ExecuteStepAsync(
            FakeRunnerStep step,
            IStepOutputSink output,
            CancellationToken cancellationToken)
        {
            foreach (var line in step.OutputLines)
            {
                await output.WriteOutputLineAsync(line, cancellationToken);
            }

            foreach (var line in step.ErrorLines)
            {
                await output.WriteErrorLineAsync(line, cancellationToken);
            }

            return new StepExecutionResult(step.ExitCode);
        }
    }

    private sealed class ThrowingRunStore : IRunStore
    {
        private readonly Exception? _initializeException;
        private readonly Exception? _openStepLogException;

        public ThrowingRunStore(Exception? initializeException = null, Exception? openStepLogException = null)
        {
            _initializeException = initializeException;
            _openStepLogException = openStepLogException;
        }

        public string CreateRunId()
        {
            return "run-1";
        }

        public Task<RunStoragePaths> InitializeRunAsync(string runId, CancellationToken cancellationToken = default)
        {
            if (_initializeException is not null)
            {
                throw _initializeException;
            }

            return Task.FromResult(new RunStoragePaths(runId, null, null));
        }

        public Task<IStepLog> OpenStepLogAsync(
            string runId,
            string jobName,
            int stepIndex,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            if (_openStepLogException is not null)
            {
                throw _openStepLogException;
            }

            return Task.FromResult<IStepLog>(NullStepLog.Instance);
        }

        public Task<ArtifactSaveResult> SaveArtifactsAsync(
            string runId,
            string jobName,
            string projectRoot,
            IReadOnlyList<WorkflowArtifact> artifacts,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArtifactSaveResult([], []));
        }

        public Task SaveRunRecordAsync(WorkflowRunRecord runRecord, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunStore : IRunStore
    {
        public List<WorkflowRunRecord> SavedRecords { get; } = [];

        public string CreateRunId()
        {
            return "run-1";
        }

        public Task<RunStoragePaths> InitializeRunAsync(string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RunStoragePaths(runId, null, null));
        }

        public Task<IStepLog> OpenStepLogAsync(
            string runId,
            string jobName,
            int stepIndex,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IStepLog>(NullStepLog.Instance);
        }

        public Task<ArtifactSaveResult> SaveArtifactsAsync(
            string runId,
            string jobName,
            string projectRoot,
            IReadOnlyList<WorkflowArtifact> artifacts,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArtifactSaveResult([], []));
        }

        public Task SaveRunRecordAsync(WorkflowRunRecord runRecord, CancellationToken cancellationToken = default)
        {
            SavedRecords.Add(runRecord);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActionCache : IActionCache
    {
        public List<LocalActionCacheRequest> Requests { get; } = [];

        public Task<ActionCacheEntry> GetOrAddLocalActionAsync(
            LocalActionCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ActionCacheEntry(
                request.ContentHash,
                "local",
                request.Uses,
                request.SourcePath,
                request.ContentHash,
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ActionCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ActionCacheEntry>>([]);
        }

        public Task<int> CleanAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class FakeRunnerStep
    {
        public FakeRunnerStep(
            int exitCode,
            IReadOnlyList<string>? outputLines = null,
            IReadOnlyList<string>? errorLines = null)
        {
            ExitCode = exitCode;
            OutputLines = outputLines ?? [];
            ErrorLines = errorLines ?? [];
        }

        public int ExitCode { get; }

        public IReadOnlyList<string> OutputLines { get; }

        public IReadOnlyList<string> ErrorLines { get; }
    }
}
