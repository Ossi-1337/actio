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
    public async Task ExecuteAsync_SavesTriggerMetadataInRunRecords()
    {
        var runner = new FakeRunnerProvider([0]);
        var store = new RecordingRunStore();
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new[]
            {
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)])
            }.ToDictionary(job => job.Name, StringComparer.Ordinal),
            [new WorkflowTrigger("push", null)]);

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success);
        Assert.All(store.SavedRecords, record => Assert.Equal("push", Assert.Single(record.Triggers).EventName));
    }

    [Fact]
    public async Task ExecuteAsync_UsesJobDisplayNameInOutputAndRunRecord()
    {
        var runner = new FakeRunnerProvider([0]);
        var store = new RecordingRunStore();
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new WorkflowJob(
                    "test",
                    "Run tests",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    WorkflowRunDefaults.Empty,
                    new Dictionary<string, string>(),
                    [],
                    [new WorkflowStep("Test", "dotnet test", null)])
            });

        using var output = new StringWriter();

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            output,
            TextWriter.Null);

        Assert.True(result.Success);
        var job = Assert.Single(store.SavedRecords.Last().Jobs);
        Assert.Equal("test", job.Id);
        Assert.Equal("Run tests", job.Name);
        Assert.Contains("[Run tests] Test", output.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_MergesStepEnvAndPassesEffectiveRunDefaultsToRunner()
    {
        var runner = new FakeRunnerProvider([0]);
        var store = new RecordingRunStore();
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["DOTNET_NOLOGO"] = "false",
                ["WORKFLOW_ONLY"] = "workflow"
            },
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new WorkflowJob(
                    "test",
                    null,
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>
                    {
                        ["DOTNET_NOLOGO"] = "true",
                        ["JOB_ONLY"] = "job"
                    },
                    new WorkflowRunDefaults("bash", "src/Actio.Core"),
                    new Dictionary<string, string>(),
                    [],
                    [
                        new WorkflowStep(
                            "Test",
                            "dotnet test",
                            null,
                            Id: "test_step",
                            Env: new Dictionary<string, string>
                            {
                                ["DOTNET_NOLOGO"] = "step",
                                ["STEP_ONLY"] = "step"
                            },
                            Shell: "sh",
                            WorkingDirectory: "tests/Actio.Core.Tests")
                    ])
            },
            [],
            new WorkflowRunDefaults("sh", "src"));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("step", request.Environment["DOTNET_NOLOGO"]);
        Assert.Equal("workflow", request.Environment["WORKFLOW_ONLY"]);
        Assert.Equal("job", request.Environment["JOB_ONLY"]);
        Assert.Equal("step", request.Environment["STEP_ONLY"]);
        Assert.Equal("sh", request.Shell);
        Assert.Equal("tests/Actio.Core.Tests", request.WorkingDirectory);
        var stepRecord = Assert.Single(Assert.Single(store.SavedRecords.Last().Jobs).Steps);
        Assert.Equal("test_step", stepRecord.Id);
        Assert.Equal("sh", stepRecord.Shell);
        Assert.Equal("tests/Actio.Core.Tests", stepRecord.WorkingDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_ExposesStepOutputsToLaterStepEnvironment()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(0, ["actio.output changed-files=src/Actio.Core"]),
                new FakeRunnerStep(0)
            ]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("Detect changes", "echo actio.output changed-files=src/Actio.Core", null, Id: "detect-changes"),
                    new WorkflowStep("Use changes", "echo later", null)
                ]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("src/Actio.Core", runner.Requests[1].Environment["ACTIO_STEP_DETECT_CHANGES_OUTPUT_CHANGED_FILES"]);
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
    public async Task ExecuteAsync_AllowsWorkflowToContinueWhenJobFailureIsContinueOnError()
    {
        var runner = new FakeRunnerProvider([42, 0]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "allow_failure",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Allowed failure", "exit 42", null)])
            {
                ContinueOnError = true
            },
            new WorkflowJob(
                "after",
                ["allow_failure"],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("After", "dotnet test", null)]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(1, result.FailedSteps);
        Assert.Equal(["allow_failure", "after"], runner.Requests.Select(request => request.JobName));
        var jobs = store.SavedRecords.Last().Jobs;
        Assert.Equal("Failed", jobs[0].Status);
        Assert.True(jobs[0].ContinueOnError);
        Assert.Equal("Success", jobs[1].Status);
    }

    [Fact]
    public async Task ExecuteAsync_MarksJobAndStepTimedOutWhenJobTimeoutExpires()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0, delay: TimeSpan.FromSeconds(5))]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Long test", "dotnet test", null)])
            {
                TimeoutMinutes = 1
            });

        var result = await new WorkflowExecutor(
            runner,
            store,
            createJobTimeout: _ => TimeSpan.FromMilliseconds(20)).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions("C:\\repo"),
                TextWriter.Null,
                TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedSteps);
        Assert.Contains(result.Errors, error => error.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        var job = Assert.Single(store.SavedRecords.Last().Jobs);
        Assert.Equal("TimedOut", job.Status);
        Assert.Equal(1, job.TimeoutMinutes);
        Assert.Equal("TimedOut", Assert.Single(job.Steps).Status);
    }

    [Fact]
    public async Task ExecuteAsync_SavesJobConcurrencyMetadata()
    {
        var runner = new FakeRunnerProvider([0]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "deploy",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Deploy", "./deploy.sh", null)])
            {
                Concurrency = new WorkflowJobConcurrency("deploy-main", true)
            });

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var job = Assert.Single(store.SavedRecords.Last().Jobs);
        Assert.Equal("deploy-main", job.ConcurrencyGroup);
        Assert.True(job.ConcurrencyCancelInProgress);
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
    public async Task ExecuteAsync_EvaluatesBooleanAndComparisonConditions()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(0, ["actio.output changed-count=2"]),
                new FakeRunnerStep(0)
            ]);
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["prepare"] = new(
                    "prepare",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Detect changes", "echo actio.output changed-count=2", null)]),
                ["test"] = new(
                    "test",
                    ["prepare"],
                    "${{ inputs.environment == 'staging' && needs.prepare.outputs.changed-count >= 2 && github.event.event_name == 'workflow_dispatch' }}",
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)])
            },
            [
                new WorkflowTrigger(
                    "workflow_dispatch",
                    null,
                    Dispatch: new WorkflowDispatch(new Dictionary<string, WorkflowDispatchInput>
                    {
                        ["environment"] = new("environment", null, true, null, "string", [])
                    }))
            ]);

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(
                "C:\\repo",
                RunTrigger: new WorkflowRunTrigger(
                    "workflow_dispatch",
                    "CLI",
                    new Dictionary<string, string> { ["environment"] = "staging" })),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["prepare", "test"], runner.Requests.Select(request => request.JobName));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsStepWhenConditionIsFalse()
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
                [
                    new WorkflowStep("Only on failure", "echo failed", null, If: "${{ failure() }}"),
                    new WorkflowStep("After", "dotnet test", null)
                ]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(1, result.SkippedSteps);
        Assert.Equal("After", Assert.Single(runner.Requests).StepName);
        var steps = Assert.Single(store.SavedRecords.Last().Jobs).Steps;
        Assert.Equal("Skipped", steps[0].Status);
        Assert.Equal("${{ failure() }}", steps[0].If);
        Assert.Equal("Success", steps[1].Status);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesAfterStepFailureWhenStepIsContinueOnError()
    {
        var runner = new FakeRunnerProvider([42, 0]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("Allowed failure", "exit 42", null, ContinueOnError: true),
                    new WorkflowStep("Report failure", "echo failed", null, If: "${{ failure() }}")
                ]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(0, result.FailedSteps);
        Assert.Equal(1, result.ContinuedSteps);
        Assert.Equal(["Allowed failure", "Report failure"], runner.Requests.Select(request => request.StepName));
        var job = Assert.Single(store.SavedRecords.Last().Jobs);
        Assert.Equal("Success", job.Status);
        Assert.Equal(["Failed", "Success"], job.Steps.Select(step => step.Status));
        Assert.True(job.Steps[0].ContinueOnError);
    }

    [Fact]
    public async Task ExecuteAsync_RunsFailureConditionStepAfterHardStepFailure()
    {
        var runner = new FakeRunnerProvider([42, 0]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("Fail", "exit 42", null),
                    new WorkflowStep("Cleanup", "echo cleanup", null, If: "${{ failure() && true }}"),
                    new WorkflowStep("Normal after failure", "echo normal", null)
                ]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(1, result.FailedSteps);
        Assert.Equal(1, result.SkippedSteps);
        Assert.Equal(["Fail", "Cleanup"], runner.Requests.Select(request => request.StepName));
        var steps = Assert.Single(store.SavedRecords.Last().Jobs).Steps;
        Assert.Equal(["Failed", "Success", "Skipped"], steps.Select(step => step.Status));
    }

    [Fact]
    public async Task ExecuteAsync_MarksStepTimedOutWhenStepTimeoutExpires()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0, delay: TimeSpan.FromSeconds(5))]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Long test", "dotnet test", null, TimeoutMinutes: 1)]));

        var result = await new WorkflowExecutor(
            runner,
            store,
            createJobTimeout: _ => TimeSpan.FromMilliseconds(20)).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions("C:\\repo"),
                TextWriter.Null,
                TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedSteps);
        Assert.Contains(result.Errors, error => error.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        var step = Assert.Single(Assert.Single(store.SavedRecords.Last().Jobs).Steps);
        Assert.Equal("TimedOut", step.Status);
        Assert.Equal(1, step.TimeoutMinutes);
    }

    [Fact]
    public async Task ExecuteAsync_EvaluatesIfConditionFromWorkflowDispatchInputs()
    {
        var runner = new FakeRunnerProvider([0]);
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new(
                    "test",
                    [],
                    "${{ inputs.environment == 'staging' }}",
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)])
            },
            [
                new WorkflowTrigger(
                    "workflow_dispatch",
                    null,
                    Dispatch: new WorkflowDispatch(new Dictionary<string, WorkflowDispatchInput>
                    {
                        ["environment"] = new("environment", null, true, null, "string", [])
                    }))
            ]);

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(
                "C:\\repo",
                RunTrigger: new WorkflowRunTrigger(
                    "workflow_dispatch",
                    "CLI",
                    new Dictionary<string, string> { ["environment"] = "staging" })),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var request = Assert.Single(runner.Requests);
        Assert.Equal("test", request.JobName);
    }

    [Fact]
    public async Task ExecuteAsync_EvaluatesIfConditionFromEventPayload()
    {
        var runner = new FakeRunnerProvider([0]);
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new(
                    "test",
                    [],
                    "${{ github.event.event_name == 'workflow_dispatch' }}",
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)])
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var request = Assert.Single(runner.Requests);
        Assert.Equal("test", request.JobName);
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

    [Fact]
    public async Task ExecuteAsync_BindsInputsForLocalCompositeAction()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "hello"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "hello", "action.yml"),
            """
            name: Hello
            inputs:
              name:
                required: true
              punctuation:
                default: "!"
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo "${{ inputs.name }}${{ inputs.punctuation }}"
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var cache = new RecordingActionCache();
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [
                        new WorkflowStep(
                            "Use hello",
                            null,
                            "./.actio/actions/hello",
                            With: new Dictionary<string, string>
                            {
                                ["name"] = "Actio"
                            })
                    ]));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var request = Assert.Single(runner.Requests);
            Assert.Equal("echo \"Actio!\"", request.Command);
            Assert.Equal("Actio", request.Environment["INPUT_NAME"]);
            Assert.Equal("!", request.Environment["INPUT_PUNCTUATION"]);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsLocalCompositeActionWhenRequiredInputIsMissing()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "hello"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "hello", "action.yml"),
            """
            name: Hello
            inputs:
              name:
                required: true
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo "${{ inputs.name }}"
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
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

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Empty(cache.Requests);
            Assert.Contains(result.Errors, error => error.Contains("action.inputs.name is required", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsLocalCompositeActionWhenInterpolationUsesUnsupportedContext()
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
                - name: Greet
                  run: echo "${{ env.NAME }}"
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
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

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Empty(cache.Requests);
            Assert.Contains(result.Errors, error => error.Contains("Unsupported expression reference 'env.NAME'", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsDockerImageAction()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
        var cache = new RecordingActionCache();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep(
                        "Use image",
                        null,
                        "docker://alpine:3.20",
                        With: new Dictionary<string, string>
                        {
                            ["node-version"] = "22"
                        })
                ]));

        var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(runner.Requests);
        var request = Assert.Single(runner.DockerActionRequests);
        Assert.Equal("alpine:3.20", request.Image);
        Assert.Equal("true", request.Environment["DOTNET_NOLOGO"]);
        Assert.Equal("22", request.Environment["INPUT_NODE_VERSION"]);
        Assert.Equal("alpine:3.20", Assert.Single(cache.DockerImageRequests).Image);
    }

    [Fact]
    public async Task ExecuteAsync_FailsWorkflowWhenDockerImageActionFails()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(42)]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Use image", null, "docker://alpine:3.20")]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Single(runner.DockerActionRequests);
        Assert.Contains(result.Errors, error => error.Contains("exit code 42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_FailsGitHubActionWhenNoSourceProviderIsConfigured()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Use GitHub action", null, "owner/repo/action@v1")]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Empty(runner.DockerActionRequests);
        Assert.Contains(result.Errors, error => error.Contains("no GitHub action source provider is configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_RunsGitHubCompositeAction()
    {
        var actionRoot = Path.Combine(Path.GetTempPath(), $"actio-github-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(actionRoot);
        var actionPath = Path.Combine(actionRoot, "action.yml");
        await File.WriteAllTextAsync(
            actionPath,
            """
            name: Remote hello
            inputs:
              name:
                required: true
              punctuation:
                default: "!"
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo "${{ inputs.name }}${{ inputs.punctuation }}"
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var cache = new RecordingActionCache();
            cache.GitHubSourceResult = GitHubActionSourceResult.Resolved(
                actionPath,
                actionRoot,
                new ActionCacheEntry(
                    "key",
                    "github",
                    "owner/repo/action@v1",
                    actionPath,
                    new string('a', 40),
                    actionRoot,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    new string('a', 40),
                    "v1"));
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [
                        new WorkflowStep(
                            "Use GitHub action",
                            null,
                            "owner/repo/action@v1",
                            With: new Dictionary<string, string>
                            {
                                ["name"] = "Remote"
                            })
                    ]));

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(Environment.CurrentDirectory),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var sourceRequest = Assert.Single(cache.GitHubSourceRequests);
            Assert.Equal("owner", sourceRequest.Owner);
            Assert.Equal("repo", sourceRequest.Repository);
            Assert.Equal("action", sourceRequest.ActionPath);
            Assert.Equal("v1", sourceRequest.Ref);
            var request = Assert.Single(runner.Requests);
            Assert.Equal("echo \"Remote!\"", request.Command);
            Assert.Equal("Remote", request.Environment["INPUT_NAME"]);
            Assert.Equal("!", request.Environment["INPUT_PUNCTUATION"]);
            Assert.Equal("/actio/action", request.Environment["GITHUB_ACTION_PATH"]);
            var mount = Assert.Single(request.AdditionalMounts);
            Assert.Equal(actionRoot, mount.HostPath);
            Assert.Equal("/actio/action", mount.ContainerPath);
            Assert.True(mount.ReadOnly);
        }
        finally
        {
            Directory.Delete(actionRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsCheckoutShimWithoutDownloadingGitHubAction()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
        var cache = new RecordingActionCache();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Checkout", null, "actions/checkout@v4")]));

        var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(cache.GitHubSourceRequests);
        var request = Assert.Single(runner.Requests);
        Assert.Contains("Actio checkout shim", request.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(request.Environment.Keys, key => string.Equals(key, "GITHUB_ACTION_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_FailsCheckoutShimWhenWithInputsAreProvided()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
        var cache = new RecordingActionCache();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep(
                        "Checkout",
                        null,
                        "actions/checkout@v4",
                        With: new Dictionary<string, string>
                        {
                            ["path"] = "src"
                        })
                ]));

        var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(cache.GitHubSourceRequests);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("checkout@v4 with inputs is not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_FailsUnsupportedGitHubActionBeforeExecutingRunner()
    {
        var actionRoot = Path.Combine(Path.GetTempPath(), $"actio-github-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(actionRoot);
        var actionPath = Path.Combine(actionRoot, "action.yml");
        await File.WriteAllTextAsync(
            actionPath,
            """
            name: JavaScript action
            runs:
              using: node20
              main: index.js
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var cache = new RecordingActionCache
            {
                GitHubSourceResult = GitHubActionSourceResult.Resolved(
                    actionPath,
                    actionRoot,
                    new ActionCacheEntry(
                        "key",
                        "github",
                        "actions/setup-node@v4",
                        actionPath,
                        new string('b', 40),
                        actionRoot,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        new string('b', 40),
                        "v4"))
            };
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Use setup-node", null, "actions/setup-node@v4")]));

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(Environment.CurrentDirectory),
                TextWriter.Null,
                TextWriter.Null);

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Empty(runner.DockerActionRequests);
            Assert.Contains(result.Errors, error => error.Contains("supports only 'composite'", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(actionRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsWhenGitHubActionSourceCannotBeResolved()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
        var cache = new RecordingActionCache
        {
            GitHubSourceResult = GitHubActionSourceResult.Failed(["repository not found"])
        };
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Use GitHub action", null, "owner/repo/action@v1")]));

        var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("repository not found", StringComparison.OrdinalIgnoreCase));
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

        public List<DockerActionExecutionRequest> DockerActionRequests { get; } = [];

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

        public Task<StepExecutionResult> ExecuteDockerActionAsync(
            DockerActionExecutionRequest request,
            IStepOutputSink output,
            CancellationToken cancellationToken = default)
        {
            DockerActionRequests.Add(request);
            return ExecuteStepAsync(_steps.Dequeue(), output, cancellationToken);
        }

        private static async Task<StepExecutionResult> ExecuteStepAsync(
            FakeRunnerStep step,
            IStepOutputSink output,
            CancellationToken cancellationToken)
        {
            if (step.Delay > TimeSpan.Zero)
            {
                await Task.Delay(step.Delay, cancellationToken);
            }

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

    private sealed class RecordingActionCache : IActionCache, IGitHubActionSourceProvider
    {
        public List<LocalActionCacheRequest> Requests { get; } = [];

        public List<DockerImageActionCacheRequest> DockerImageRequests { get; } = [];

        public List<GitHubActionSourceRequest> GitHubSourceRequests { get; } = [];

        public GitHubActionSourceResult? GitHubSourceResult { get; set; }

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

        public Task<ActionCacheEntry> GetOrAddDockerImageActionAsync(
            DockerImageActionCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            DockerImageRequests.Add(request);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ActionCacheEntry(
                request.Image,
                "docker",
                request.Uses,
                request.Image,
                request.Image,
                string.Empty,
                now,
                now,
                request.IsPinned ? request.Image : null,
                request.IsPinned ? null : request.MutablePart));
        }

        public Task<GitHubActionSourceResult> GetGitHubActionSourceAsync(
            GitHubActionSourceRequest request,
            CancellationToken cancellationToken = default)
        {
            GitHubSourceRequests.Add(request);
            return Task.FromResult(
                GitHubSourceResult ??
                GitHubActionSourceResult.Failed(["GitHub source result was not configured."]));
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
            IReadOnlyList<string>? errorLines = null,
            TimeSpan? delay = null)
        {
            ExitCode = exitCode;
            OutputLines = outputLines ?? [];
            ErrorLines = errorLines ?? [];
            Delay = delay ?? TimeSpan.Zero;
        }

        public int ExitCode { get; }

        public IReadOnlyList<string> OutputLines { get; }

        public IReadOnlyList<string> ErrorLines { get; }

        public TimeSpan Delay { get; }
    }
}
