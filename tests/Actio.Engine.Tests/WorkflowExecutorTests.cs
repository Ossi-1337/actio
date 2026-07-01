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
    public async Task ExecuteAsync_PassesJobContainerToRunSteps()
    {
        var runner = new FakeRunnerProvider([0]);
        var projectRoot = Environment.CurrentDirectory;
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["DOTNET_NOLOGO"] = "workflow"
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
                        ["DOTNET_NOLOGO"] = "job"
                    },
                    WorkflowRunDefaults.Empty,
                    null,
                    false,
                    null,
                    WorkflowJobStrategy.Empty,
                    new Dictionary<string, string>(),
                    [],
                    [
                        new WorkflowStep(
                            "Test",
                            "npm test",
                            null,
                            Env: new Dictionary<string, string>
                            {
                                ["DOTNET_NOLOGO"] = "step"
                            })
                    ],
                    new WorkflowJobContainer(
                        "node:22",
                        new Dictionary<string, string>
                        {
                            ["CONTAINER_ONLY"] = "container",
                            ["DOTNET_NOLOGO"] = "container"
                        },
                        ["3000:3000"],
                        [new WorkflowJobContainerVolume("./.actio/cache", "/cache", ReadOnly: true)],
                        ["--cpus", "1"]))
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(projectRoot),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var request = Assert.Single(runner.Requests);
        Assert.Equal("step", request.Environment["DOTNET_NOLOGO"]);
        Assert.Equal("container", request.Environment["CONTAINER_ONLY"]);
        Assert.NotNull(request.Container);
        Assert.Equal("node:22", request.Container.Image);
        Assert.Equal(["3000:3000"], request.Container.Ports);
        Assert.Equal(["--cpus", "1"], request.Container.Options);
        var volume = Assert.Single(request.Container.Volumes);
        Assert.Equal(Path.Combine(projectRoot, "./.actio/cache"), volume.HostPath);
        Assert.Equal("/cache", volume.ContainerPath);
        Assert.True(volume.ReadOnly);
        Assert.DoesNotContain(request.AdditionalMounts, mount => string.Equals(mount.ContainerPath, "/cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_StartsServicesAndPassesNetworkToSteps()
    {
        var runner = new FakeRunnerProvider([0, 0]);
        var projectRoot = Environment.CurrentDirectory;
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new WorkflowJob(
                    "test",
                    null,
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    WorkflowRunDefaults.Empty,
                    null,
                    false,
                    null,
                    WorkflowJobStrategy.Empty,
                    new Dictionary<string, string>(),
                    [],
                    [
                        new WorkflowStep("Test", "dotnet test", null),
                        new WorkflowStep("Use image", null, "docker://alpine:3.20")
                    ],
                    null,
                    new Dictionary<string, WorkflowJobService>
                    {
                        ["postgres"] = new WorkflowJobService(
                            "postgres:16",
                            new Dictionary<string, string>
                            {
                                ["POSTGRES_PASSWORD"] = "postgres"
                            },
                            ["5432:5432"],
                            [new WorkflowJobContainerVolume("./db", "/var/lib/postgresql/data", ReadOnly: false)],
                            ["--health-cmd=pg_isready"])
                    })
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(projectRoot),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var startRequest = Assert.Single(runner.ServiceStartRequests);
        var service = Assert.Single(startRequest.Services);
        Assert.Equal("postgres", service.Name);
        Assert.Equal("postgres:16", service.Image);
        Assert.Equal("postgres", service.Environment["POSTGRES_PASSWORD"]);
        Assert.Equal(["5432:5432"], service.Ports);
        Assert.Equal(["--health-cmd=pg_isready"], service.Options);
        var serviceVolume = Assert.Single(service.Volumes);
        Assert.Equal(Path.Combine(projectRoot, "./db"), serviceVolume.HostPath);
        Assert.Equal("/var/lib/postgresql/data", serviceVolume.ContainerPath);

        var stepRequest = Assert.Single(runner.Requests);
        Assert.NotNull(stepRequest.Services);
        Assert.Equal("actio-test-network", stepRequest.Services.NetworkName);
        var dockerActionRequest = Assert.Single(runner.DockerActionRequests);
        Assert.NotNull(dockerActionRequest.Services);
        Assert.Equal("actio-test-network", dockerActionRequest.Services.NetworkName);
        var stoppedNetwork = Assert.Single(runner.StoppedServiceNetworks);
        Assert.Equal("actio-test-network", stoppedNetwork.NetworkName);
    }

    [Fact]
    public async Task ExecuteAsync_FailsJobWhenServiceStartupFails()
    {
        var runner = new FakeRunnerProvider(Array.Empty<int>());
        runner.ServiceStartResult = ServiceContainerStartResult.Failed(["Service 'postgres' did not become healthy."]);
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new WorkflowJob(
                    "test",
                    null,
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    WorkflowRunDefaults.Empty,
                    null,
                    false,
                    null,
                    WorkflowJobStrategy.Empty,
                    new Dictionary<string, string>(),
                    [],
                    [new WorkflowStep("Test", "dotnet test", null)],
                    null,
                    new Dictionary<string, WorkflowJobService>
                    {
                        ["postgres"] = new WorkflowJobService("postgres:16")
                    })
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(0, result.SuccessfulSteps);
        Assert.Equal(1, result.SkippedSteps);
        Assert.Empty(runner.Requests);
        Assert.Empty(runner.StoppedServiceNetworks);
        Assert.Contains(result.Errors, error => error.Contains("did not become healthy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_AddsDefaultEnvironmentVariablesToRunSteps()
    {
        var runner = new FakeRunnerProvider([0]);
        var projectRoot = Environment.CurrentDirectory;
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["CI"] = "custom",
                ["GITHUB_RUN_ID"] = "wrong"
            },
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>
                    {
                        ["RUNNER_OS"] = "Wrong"
                    },
                    [
                        new WorkflowStep(
                            "Run tests",
                            "dotnet test",
                            null,
                            Id: "run_tests",
                            Env: new Dictionary<string, string>
                            {
                                ["ACTIO_WORKSPACE"] = "wrong"
                            })
                    ])
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(
                projectRoot,
                RunId: "run-env",
                RunTrigger: new WorkflowRunTrigger("workflow_dispatch", "CLI")),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var request = Assert.Single(runner.Requests);
        Assert.Equal("custom", request.Environment["CI"]);
        AssertDefaultEnvironment(
            request.Environment,
            "run-env",
            "CI",
            "test",
            "run_tests",
            "Run tests",
            "workflow_dispatch",
            "CLI",
            expectedCi: "custom");
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
    public async Task ExecuteAsync_ReadsGitHubOutputFileAndExposesStepOutputs()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(
                    0,
                    onExecute: (environment, mounts) => WriteEnvironmentFile(
                        environment,
                        mounts,
                        "GITHUB_OUTPUT",
                        """
                        changed=true
                        message<<EOF
                        hello
                        world
                        EOF

                        """)),
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
                    new WorkflowStep("Detect", "echo detect", null, Id: "detect"),
                    new WorkflowStep(
                        "Use output",
                        "echo later",
                        null,
                        If: "${{ steps.detect.outputs.changed == 'true' }}")
                ]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("true", runner.Requests[1].Environment["ACTIO_STEP_DETECT_OUTPUT_CHANGED"]);
        Assert.Contains(result.Outputs, output => output.JobName == "test" && output.Name == "changed" && output.Value == "true");
        Assert.Contains(result.Outputs, output => output.JobName == "test" && output.Name == "message" && output.Value == "hello\nworld");
    }

    [Fact]
    public async Task ExecuteAsync_AppliesGitHubEnvAndPathFilesToFollowingStepsOnly()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(
                    0,
                    onExecute: (environment, mounts) =>
                    {
                        WriteEnvironmentFile(
                            environment,
                            mounts,
                            "GITHUB_ENV",
                            """
                            FROM_FILE=hello
                            MULTILINE<<EOF
                            one
                            two
                            EOF

                            """);
                        WriteEnvironmentFile(environment, mounts, "GITHUB_PATH", "/tools/bin\n");
                        WriteEnvironmentFile(environment, mounts, "GITHUB_STATE", "state=value\n");
                    }),
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
                    new WorkflowStep("Prepare env", "echo env", null),
                    new WorkflowStep("Use env", "echo later", null)
                ]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("/actio/env/GITHUB_ENV", runner.Requests[0].Environment["GITHUB_ENV"]);
        Assert.Equal("/actio/env/GITHUB_OUTPUT", runner.Requests[0].Environment["GITHUB_OUTPUT"]);
        Assert.Equal("/actio/env/GITHUB_PATH", runner.Requests[0].Environment["GITHUB_PATH"]);
        Assert.Equal("/actio/env/GITHUB_STEP_SUMMARY", runner.Requests[0].Environment["GITHUB_STEP_SUMMARY"]);
        Assert.Equal("/actio/env/GITHUB_STATE", runner.Requests[0].Environment["GITHUB_STATE"]);
        Assert.False(runner.Requests[0].Environment.ContainsKey("FROM_FILE"));
        Assert.Equal("hello", runner.Requests[1].Environment["FROM_FILE"]);
        Assert.Equal("one\ntwo", runner.Requests[1].Environment["MULTILINE"]);
        Assert.StartsWith("/tools/bin:", runner.Requests[1].Environment["PATH"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_StoresGitHubStepSummaryInRunRecord()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(
                    0,
                    onExecute: (environment, mounts) => WriteEnvironmentFile(
                        environment,
                        mounts,
                        "GITHUB_STEP_SUMMARY",
                        "### Test summary\nAll good\n"))
            ]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Summarize", "echo summary", null)]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var step = Assert.Single(Assert.Single(store.SavedRecords.Last().Jobs).Steps);
        Assert.NotNull(step.SummaryPath);
        Assert.Equal("### Test summary\nAll good\n", step.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesWorkflowCommandsAndMasksLogs()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(
                    0,
                    [
                        "::add-mask::secret-value",
                        "visible secret-value",
                        "::group::Build secret-value",
                        "::notice::notice secret-value",
                        "::debug::debug secret-value",
                        "::warning file=src/secret-value.cs,line=12,title=Careful secret-value::warning secret-value",
                        "::error::failure secret-value",
                        "::endgroup::"
                    ]),
                new FakeRunnerStep(
                    0,
                    ["after secret-value"],
                    onExecute: (environment, mounts) =>
                    {
                        WriteEnvironmentFile(environment, mounts, "GITHUB_OUTPUT", "token=secret-value\n");
                        WriteEnvironmentFile(environment, mounts, "GITHUB_STEP_SUMMARY", "summary secret-value\n");
                    })
            ]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [
                    new WorkflowStep("Annotate", "echo commands", null),
                    new WorkflowStep("Use mask", "echo mask", null, Id: "use_mask")
                ]));
        using var output = new StringWriter();

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            output,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.DoesNotContain("secret-value", output.ToString());
        Assert.DoesNotContain("secret-value", string.Join(Environment.NewLine, store.LogLines));
        Assert.Contains("[stdout] visible ***", store.LogLines);
        Assert.Contains("[stdout] [group] Build ***", store.LogLines);
        Assert.Contains("[stdout] [endgroup]", store.LogLines);
        Assert.Contains("[stdout] after ***", store.LogLines);
        Assert.Contains(result.Outputs, output =>
            output.JobName == "test" &&
            output.Name == "token" &&
            output.Value == "***");

        var steps = Assert.Single(store.SavedRecords.Last().Jobs).Steps;
        var step = steps[0];
        Assert.Equal(4, step.Annotations.Count);
        Assert.Contains(step.Annotations, annotation =>
            annotation.Level == "notice" &&
            annotation.Message == "notice ***");
        Assert.Contains(step.Annotations, annotation =>
            annotation.Level == "debug" &&
            annotation.Message == "debug ***");
        Assert.Contains(step.Annotations, annotation =>
            annotation.Level == "warning" &&
            annotation.Message == "warning ***" &&
            annotation.Title == "Careful ***" &&
            annotation.File == "src/***.cs" &&
            annotation.Line == 12);
        Assert.Contains(step.Annotations, annotation =>
            annotation.Level == "error" &&
            annotation.Message == "failure ***");
        Assert.Equal("summary ***\n", steps[1].Summary);
    }

    [Fact]
    public async Task ExecuteAsync_StopCommandsTreatsCommandsAsPlainLogTextUntilResume()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(
                    0,
                    [
                        "::stop-commands::pause",
                        "::warning::not structured",
                        "::pause::",
                        "::warning::structured"
                    ])
            ]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Stop commands", "echo commands", null)]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains("[stdout] [command] workflow commands stopped", store.LogLines);
        Assert.Contains("[stdout] ::warning::not structured", store.LogLines);
        Assert.Contains("[stdout] [command] workflow commands resumed", store.LogLines);

        var step = Assert.Single(Assert.Single(store.SavedRecords.Last().Jobs).Steps);
        var annotation = Assert.Single(step.Annotations);
        Assert.Equal("warning", annotation.Level);
        Assert.Equal("structured", annotation.Message);
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
    public async Task ExecuteAsync_SavesJobEnvironmentMetadata()
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
                Environment = new WorkflowJobEnvironment("production", "https://actio.local/deployments/42")
            });

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var environment = Assert.Single(store.SavedRecords.Last().Jobs).Environment;
        Assert.NotNull(environment);
        Assert.Equal("production", environment.Name);
        Assert.Equal("https://actio.local/deployments/42", environment.Url);
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
    public async Task ExecuteAsync_RunsJobWithAlwaysConditionAfterFailedDependency()
    {
        var runner = new FakeRunnerProvider([42, 0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "prepare",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Prepare", "exit 42", null)]),
            new WorkflowJob(
                "cleanup",
                ["prepare"],
                "${{ always() }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Cleanup", "echo cleanup", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(["prepare", "cleanup"], runner.Requests.Select(request => request.JobName));
        Assert.Equal(1, result.SuccessfulSteps);
        Assert.Equal(1, result.FailedSteps);
    }

    [Fact]
    public async Task ExecuteAsync_RunsJobWithFailureConditionAfterFailedDependency()
    {
        var runner = new FakeRunnerProvider([42, 0]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "prepare",
                [],
                null,
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Prepare", "exit 42", null)]),
            new WorkflowJob(
                "report",
                ["prepare"],
                "${{ failure() }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Report", "echo failed", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(["prepare", "report"], runner.Requests.Select(request => request.JobName));
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
    public async Task ExecuteAsync_ExpandsMatrixJobs()
    {
        var runner = new FakeRunnerProvider([0, 0]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(CreateMatrixJob(
            "test",
            [],
            "${{ matrix.os }}",
            [
                new WorkflowStep(
                    "Test",
                    "dotnet test",
                    null,
                    If: "${{ matrix.dotnet == '10.0' }}")
            ]));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(2, result.SuccessfulSteps);
        Assert.Equal(2, result.TotalSteps);
        Assert.Equal(
            ["test[dotnet=10.0,os=ubuntu-latest]", "test[dotnet=10.0,os=debian-latest]"],
            runner.Requests.Select(request => request.JobName));
        Assert.Equal(["ubuntu-latest", "debian-latest"], runner.Requests.Select(request => request.RunsOn));
        Assert.All(runner.Requests, request => Assert.Equal("10.0", request.Environment["ACTIO_MATRIX_DOTNET"]));
        Assert.Equal(["ubuntu-latest", "debian-latest"], runner.Requests.Select(request => request.Environment["ACTIO_MATRIX_OS"]));

        var jobs = store.SavedRecords.Last().Jobs;
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Equal("10.0", job.Matrix["dotnet"]));
    }

    [Fact]
    public async Task ExecuteAsync_ExpandsMatrixNeedsForDependentJobs()
    {
        var runner = new FakeRunnerProvider([0, 0, 0]);
        var workflow = CreateWorkflow(
            CreateMatrixJob(
                "test",
                [],
                "${{ matrix.os }}",
                [new WorkflowStep("Test", "dotnet test", null)]),
            new WorkflowJob(
                "publish",
                ["test"],
                "${{ needs.test.result == 'success' }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Publish", "dotnet publish", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(3, result.SuccessfulSteps);
        Assert.Equal(3, result.TotalSteps);
        Assert.Equal(
            ["test[dotnet=10.0,os=ubuntu-latest]", "test[dotnet=10.0,os=debian-latest]", "publish"],
            runner.Requests.Select(request => request.JobName));
    }

    [Fact]
    public async Task ExecuteAsync_AppliesMatrixIncludeAndExclude()
    {
        var runner = new FakeRunnerProvider([0, 0, 0, 0, 0]);
        var workflow = CreateWorkflow(CreateMatrixJob(
            "test",
            [],
            "${{ matrix.os }}",
            [new WorkflowStep("Test", "dotnet test", null)],
            new WorkflowJobStrategy(
                new WorkflowJobMatrix(
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                    {
                        ["os"] = ["ubuntu-latest", "debian-latest"],
                        ["dotnet"] = ["10.0", "9.0"]
                    },
                    Include:
                    [
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["os"] = "ubuntu-latest",
                            ["configuration"] = "Debug"
                        },
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["os"] = "alpine-latest",
                            ["dotnet"] = "10.0"
                        },
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["os"] = "debian-latest",
                            ["dotnet"] = "9.0",
                            ["configuration"] = "Release"
                        }
                    ],
                    Exclude:
                    [
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["os"] = "debian-latest",
                            ["dotnet"] = "9.0"
                        }
                    ]))));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(5, result.SuccessfulSteps);
        Assert.Equal(5, result.TotalSteps);
        Assert.Equal(
            [
                "test[configuration=Debug,dotnet=10.0,os=ubuntu-latest]",
                "test[dotnet=10.0,os=debian-latest]",
                "test[configuration=Debug,dotnet=9.0,os=ubuntu-latest]",
                "test[dotnet=10.0,os=alpine-latest]",
                "test[configuration=Release,dotnet=9.0,os=debian-latest]"
            ],
            runner.Requests.Select(request => request.JobName));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRemainingMatrixJobsWhenFailFastIsEnabled()
    {
        var runner = new FakeRunnerProvider([42]);
        var store = new RecordingRunStore();
        var workflow = CreateWorkflow(CreateMatrixJob(
            "test",
            [],
            "${{ matrix.os }}",
            [new WorkflowStep("Test", "exit 42", null)],
            CreateMatrixStrategy(["ubuntu-latest", "debian-latest", "alpine-latest"])));

        var result = await new WorkflowExecutor(runner, store).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Equal(3, result.TotalSteps);
        Assert.Equal(1, result.FailedSteps);
        Assert.Equal(2, result.SkippedSteps);
        Assert.Single(runner.Requests);

        var jobs = store.SavedRecords.Last().Jobs;
        Assert.Equal(["Failed", "Skipped", "Skipped"], jobs.Select(job => job.Status));
        Assert.Contains(jobs.Skip(1), job =>
            job.Errors.Contains("Matrix fail-fast skipped this job because another matrix job failed."));
    }

    [Fact]
    public async Task ExecuteAsync_RespectsMatrixMaxParallel()
    {
        var currentConcurrency = 0;
        var maxConcurrency = 0;
        var runner = new FakeRunnerProvider(Enumerable.Range(0, 4).Select(_ => new FakeRunnerStep(
            0,
            delay: TimeSpan.FromMilliseconds(50),
            onExecute: (_, _) =>
            {
                var current = Interlocked.Increment(ref currentConcurrency);
                int observed;
                do
                {
                    observed = maxConcurrency;
                    if (current <= observed)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref maxConcurrency, current, observed) != observed);
            },
            onComplete: () => Interlocked.Decrement(ref currentConcurrency))));
        var workflow = CreateWorkflow(CreateMatrixJob(
            "test",
            [],
            "${{ matrix.os }}",
            [new WorkflowStep("Test", "dotnet test", null)],
            CreateMatrixStrategy(["ubuntu-latest", "debian-latest", "alpine-latest", "ubuntu-22.04"], maxParallel: 2)));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(4, result.SuccessfulSteps);
        Assert.Equal(4, result.TotalSteps);
        Assert.Equal(4, runner.Requests.Count);
        Assert.Equal(2, maxConcurrency);
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
    public async Task ExecuteAsync_EvaluatesJobContextReferences()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(0, ["actio.output changed=true"]),
                new FakeRunnerStep(0)
            ]);
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["RUN_TESTS"] = "true"
            },
            new Dictionary<string, WorkflowJob>
            {
                ["prepare"] = new(
                    "prepare",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Detect changes", "echo actio.output changed=true", null)]),
                ["test"] = new(
                    "test",
                    ["prepare"],
                    "${{ env.RUN_TESTS == 'true' && github.workflow == 'CI' && github.run_id == 'run-context' && github.actor != '' && github.triggering_actor != '' && github.event_name == 'workflow_dispatch' && github.event.source == 'CLI' && runner.os == 'Linux' && needs.prepare.result == 'success' && needs.prepare.outputs.changed == 'true' }}",
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)])
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo", RunId: "run-context"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["prepare", "test"], runner.Requests.Select(request => request.JobName));
    }

    [Fact]
    public async Task ExecuteAsync_EvaluatesStepContextReferences()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(0, ["actio.output changed=true"]),
                new FakeRunnerStep(0)
            ]);
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>
            {
                ["WORKFLOW_FLAG"] = "true"
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
                        ["JOB_FLAG"] = "true"
                    },
                    WorkflowRunDefaults.Empty,
                    new Dictionary<string, string>(),
                    [],
                    [
                        new WorkflowStep("Detect", "echo actio.output changed=true", null, Id: "detect"),
                        new WorkflowStep(
                            "Use context",
                            "dotnet test",
                            null,
                            Env: new Dictionary<string, string>
                            {
                                ["STEP_FLAG"] = "true"
                            },
                            If: "${{ env.WORKFLOW_FLAG == 'true' && env.JOB_FLAG == 'true' && env.STEP_FLAG == 'true' && job.status == 'running' && step.name == 'Use context' && runner.name == 'ubuntu-latest' && steps.detect.outputs.changed == 'true' && steps.detect.outcome == 'success' }}")
                    ])
            });

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions("C:\\repo"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["Detect", "Use context"], runner.Requests.Select(request => request.StepName));
    }

    [Fact]
    public async Task ExecuteAsync_EvaluatesHashFilesCondition()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-hashfiles-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "src", "app.cs"), "class App {}");

        try
        {
            var runner = new FakeRunnerProvider([0]);
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    "${{ hashFiles('**/*.cs') != '' }}",
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)]));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var request = Assert.Single(runner.Requests);
            Assert.Equal("test", request.JobName);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
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
            var runner = new FakeRunnerProvider(
                [
                    new FakeRunnerStep(0),
                    new FakeRunnerStep(0, ["actio.output greeting=hello"])
                ]);
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
            Assert.Equal(["echo first", "echo \"actio.output greeting=hello\""], runner.Requests.Select(request => request.Command));
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
                  run: echo "${{ format('{0}{1}', inputs.name, inputs.punctuation) }}"
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
    public async Task ExecuteAsync_InterpolatesLocalVarsAndSecretsInActionWith()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "secure"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "secure", "action.yml"),
            """
            name: Secure
            inputs:
              token:
                required: true
              mode:
                required: true
            runs:
              using: composite
              steps:
                - name: Use token
                  run: echo "${{ inputs.token }} ${{ inputs.mode }}"
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0, ["token secret-value"])]);
            var store = new RecordingRunStore();
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [
                        new WorkflowStep(
                            "Use secure action",
                            null,
                            "./.actio/actions/secure",
                            With: new Dictionary<string, string>
                            {
                                ["token"] = "${{ secrets.NUGET_TOKEN }}",
                                ["mode"] = "${{ vars.CONFIGURATION }}"
                            })
                    ]));

            using var output = new StringWriter();
            var result = await new WorkflowExecutor(runner, runStore: store).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(
                    projectRoot,
                    "workflow.yml",
                    "run-secure",
                    Secrets: new Dictionary<string, string> { ["NUGET_TOKEN"] = "secret-value" },
                    Variables: new Dictionary<string, string> { ["CONFIGURATION"] = "Release" }),
                output,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var request = Assert.Single(runner.Requests);
            Assert.Equal("secret-value", request.Environment["INPUT_TOKEN"]);
            Assert.Equal("Release", request.Environment["INPUT_MODE"]);
            Assert.Contains("token ***", output.ToString());
            Assert.DoesNotContain("secret-value", output.ToString());

            var stepRecord = Assert.Single(Assert.Single(store.SavedRecords.Last().Jobs).Steps);
            Assert.Contains("*** Release", stepRecord.Command);
            Assert.DoesNotContain("secret-value", stepRecord.Command);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsClearlyWhenActionWithReferencesMissingGithubToken()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "secure"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "secure", "action.yml"),
            """
            name: Secure
            inputs:
              token:
                required: true
            runs:
              using: composite
              steps:
                - name: Use token
                  run: echo ready
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [
                        new WorkflowStep(
                            "Use secure action",
                            null,
                            "./.actio/actions/secure",
                            With: new Dictionary<string, string>
                            {
                                ["token"] = "${{ secrets.GITHUB_TOKEN }}"
                            })
                    ]));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Contains(result.Errors, error => error.Contains("Actio does not create GitHub's automatic GITHUB_TOKEN", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCompositeActionOutputs()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "outputs"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "outputs", "action.yml"),
            """
            name: Output action
            inputs:
              name:
                default: Actio
            outputs:
              result:
                value: "${{ steps.produce.outputs.result }}"
              greeting:
                value: "hello ${{ inputs.name }}"
            runs:
              using: composite
              steps:
                - id: produce
                  name: Produce
                  shell: bash
                  working-directory: tools
                  run: echo produce
                - name: Consume
                  shell: sh
                  run: echo consume
            """);

        try
        {
            var runner = new FakeRunnerProvider(
                [
                    new FakeRunnerStep(
                        0,
                        onExecute: (environment, mounts) => WriteEnvironmentFile(
                            environment,
                            mounts,
                            "GITHUB_OUTPUT",
                            "result=passed\n")),
                    new FakeRunnerStep(0),
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
                        new WorkflowStep("Use composite", null, "./.actio/actions/outputs", Id: "composite"),
                        new WorkflowStep(
                            "Use composite output",
                            "echo later",
                            null,
                            If: "${{ steps.composite.outputs.result == 'passed' }}")
                    ]));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(2, result.SuccessfulSteps);
            Assert.Equal(["Use composite / Produce", "Use composite / Consume", "Use composite output"], runner.Requests.Select(request => request.StepName));
            Assert.Equal("bash", runner.Requests[0].Shell);
            Assert.Equal("tools", runner.Requests[0].WorkingDirectory);
            Assert.Equal("sh", runner.Requests[1].Shell);
            Assert.Equal("passed", runner.Requests[1].Environment["ACTIO_STEP_PRODUCE_OUTPUT_RESULT"]);
            Assert.Equal("passed", runner.Requests[2].Environment["ACTIO_STEP_COMPOSITE_OUTPUT_RESULT"]);
            Assert.Contains(result.Outputs, output => output.JobName == "test" && output.Name == "result" && output.Value == "passed");
            Assert.Contains(result.Outputs, output => output.JobName == "test" && output.Name == "greeting" && output.Value == "hello Actio");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsNestedLocalCompositeAction()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        var parentRoot = Path.Combine(projectRoot, ".actio", "actions", "parent");
        var childRoot = Path.Combine(parentRoot, "child");
        Directory.CreateDirectory(childRoot);
        await File.WriteAllTextAsync(
            Path.Combine(parentRoot, "action.yml"),
            """
            name: Parent
            inputs:
              name:
                required: true
            outputs:
              result:
                value: "${{ steps.child.outputs.result }}"
            runs:
              using: composite
              steps:
                - id: child
                  name: Child
                  uses: ./child
                  with:
                    name: "${{ inputs.name }}"
            """);
        await File.WriteAllTextAsync(
            Path.Combine(childRoot, "action.yml"),
            """
            name: Child
            inputs:
              name:
                required: true
            outputs:
              result:
                value: "${{ steps.produce.outputs.result }}"
            runs:
              using: composite
              steps:
                - id: produce
                  name: Produce
                  run: echo "${{ inputs.name }}"
            """);

        try
        {
            var runner = new FakeRunnerProvider(
                [
                    new FakeRunnerStep(
                        0,
                        onExecute: (environment, mounts) =>
                        {
                            Assert.Equal("Nested", environment["INPUT_NAME"]);
                            WriteEnvironmentFile(environment, mounts, "GITHUB_OUTPUT", "result=Nested\n");
                        }),
                    new FakeRunnerStep(0)
                ]);
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
                            "Use parent",
                            null,
                            "./.actio/actions/parent",
                            Id: "parent",
                            With: new Dictionary<string, string>
                            {
                                ["name"] = "Nested"
                            }),
                        new WorkflowStep(
                            "Use parent output",
                            "echo later",
                            null,
                            If: "${{ steps.parent.outputs.result == 'Nested' }}")
                    ]));

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(2, result.SuccessfulSteps);
            Assert.Equal(["Use parent / Child / Produce", "Use parent output"], runner.Requests.Select(request => request.StepName));
            Assert.Contains(cache.Requests, request => request.Uses == "./.actio/actions/parent");
            Assert.Contains(cache.Requests, request => request.Uses == "./child");
            Assert.Contains(result.Outputs, output => output.JobName == "test" && output.Name == "result" && output.Value == "Nested");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DetectsNestedActionCyclesBeforeExecution()
    {
        var actionRoot = Path.Combine(Path.GetTempPath(), $"actio-action-cycle-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(actionRoot);
        var actionPath = Path.Combine(actionRoot, "action.yml");
        await File.WriteAllTextAsync(
            actionPath,
            """
            name: Self
            runs:
              using: composite
              steps:
                - name: Self
                  uses: owner/repo@v1
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
                    "owner/repo@v1",
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
                    [new WorkflowStep("Use self", null, "owner/repo@v1")]));

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(Environment.CurrentDirectory),
                TextWriter.Null,
                TextWriter.Null);

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Contains(result.Errors, error => error.Contains("nested action cycle detected", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(actionRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsNestedDockerImageActionWithMutableWarning()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "parent"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "parent", "action.yml"),
            """
            name: Parent
            runs:
              using: composite
              steps:
                - name: Nested image
                  uses: docker://alpine:3.20
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var cache = new RecordingActionCache();
            using var error = new StringWriter();
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Use parent", null, "./.actio/actions/parent")]));

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                error);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var request = Assert.Single(runner.DockerActionRequests);
            Assert.Equal("alpine:3.20", request.Image);
            Assert.Contains("mutable Docker image reference", error.ToString(), StringComparison.OrdinalIgnoreCase);
            var cacheRequest = Assert.Single(cache.DockerImageRequests);
            Assert.Equal("docker://alpine:3.20", cacheRequest.Uses);
            Assert.False(cacheRequest.IsPinned);
            Assert.Equal("3.20", cacheRequest.MutablePart);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsCompositeActionWithActionStepName()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".actio", "actions", "failure"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, ".actio", "actions", "failure", "action.yml"),
            """
            name: Failure action
            runs:
              using: composite
              steps:
                - name: Prepare
                  run: echo prepare
                - name: Fail inside
                  run: exit 42
            """);

        try
        {
            var runner = new FakeRunnerProvider([0, 42]);
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Use failing action", null, "./.actio/actions/failure")]));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.False(result.Success);
            Assert.Equal(1, result.FailedSteps);
            Assert.Equal(1, result.TotalSteps);
            Assert.Equal(["Use failing action / Prepare", "Use failing action / Fail inside"], runner.Requests.Select(request => request.StepName));
            Assert.Contains(result.Errors, error => error.Contains("action step 'Fail inside' failed with exit code 42", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsLocalJavaScriptAction()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        var actionRoot = Path.Combine(projectRoot, ".actio", "actions", "hello");
        Directory.CreateDirectory(Path.Combine(actionRoot, "dist"));
        await File.WriteAllTextAsync(
            Path.Combine(actionRoot, "action.yml"),
            """
            name: Hello
            inputs:
              name:
                required: true
            runs:
              using: node20
              pre: dist/pre.js
              main: dist/index.js
              post: dist/post.js
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

            var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Empty(runner.Requests);
            var request = Assert.Single(runner.JavaScriptActionRequests);
            Assert.Equal("/actio/action", request.ActionPath);
            Assert.Equal("dist/index.js", request.Main);
            Assert.Equal("dist/pre.js", request.Pre);
            Assert.Equal("dist/post.js", request.Post);
            Assert.Equal("Actio", request.Environment["INPUT_NAME"]);
            Assert.Equal("/actio/action", request.Environment["GITHUB_ACTION_PATH"]);
            var actionMount = Assert.Single(request.AdditionalMounts, mount => mount.ContainerPath == "/actio/action");
            Assert.Equal(actionRoot, actionMount.HostPath);
            Assert.True(actionMount.ReadOnly);
            Assert.Equal("./.actio/actions/hello", cache.Requests[0].Uses);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsLocalDockerfileAction()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        var actionRoot = Path.Combine(projectRoot, ".actio", "actions", "hello");
        Directory.CreateDirectory(actionRoot);
        await File.WriteAllTextAsync(
            Path.Combine(actionRoot, "action.yml"),
            """
            name: Hello
            inputs:
              name:
                default: Actio
            runs:
              using: docker
              image: Dockerfile
            """);
        await File.WriteAllTextAsync(
            Path.Combine(actionRoot, "Dockerfile"),
            """
            FROM alpine:3.20
            CMD ["sh", "-c", "echo hello"]
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

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Empty(runner.Requests);
            Assert.Empty(runner.DockerActionRequests);
            var cacheRequest = Assert.Single(cache.DockerfileRequests);
            Assert.Equal("./.actio/actions/hello", cacheRequest.Uses);
            Assert.Equal(actionRoot, cacheRequest.ActionDirectory);
            Assert.Equal(Path.Combine(actionRoot, "Dockerfile"), cacheRequest.DockerfilePath);
            Assert.Equal(64, cacheRequest.ContentHash.Length);

            var request = Assert.Single(runner.DockerfileActionRequests);
            Assert.Equal($"actio/action:{cacheRequest.ContentHash}", request.Image);
            Assert.Equal(actionRoot, request.BuildContext);
            Assert.Equal(Path.Combine(actionRoot, "Dockerfile"), request.DockerfilePath);
            Assert.Equal("Actio", request.Environment["INPUT_NAME"]);
            Assert.Equal("/actio/action", request.Environment["GITHUB_ACTION_PATH"]);
            var actionMount = Assert.Single(request.AdditionalMounts, mount => mount.ContainerPath == "/actio/action");
            Assert.Equal(actionRoot, actionMount.HostPath);
            Assert.True(actionMount.ReadOnly);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailsLocalDockerfileActionWhenDockerfileIsMissing()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-action-tests-{Guid.NewGuid():N}");
        var actionRoot = Path.Combine(projectRoot, ".actio", "actions", "hello");
        Directory.CreateDirectory(actionRoot);
        await File.WriteAllTextAsync(
            Path.Combine(actionRoot, "action.yml"),
            """
            name: Hello
            runs:
              using: docker
              image: Dockerfile
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
            Assert.Empty(cache.DockerfileRequests);
            Assert.Empty(runner.DockerfileActionRequests);
            Assert.Contains(result.Errors, error => error.Contains("no Dockerfile exists", StringComparison.OrdinalIgnoreCase));
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
            Assert.Contains(result.Errors, error => error.Contains("Unsupported expression context 'env'", StringComparison.OrdinalIgnoreCase));
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
                            ["args"] = "\"hello world\" --count 2",
                            ["entrypoint"] = "/bin/echo",
                            ["node-version"] = "22"
                        })
                ]));

        var result = await new WorkflowExecutor(runner, actionCache: cache).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory, RunId: "run-docker-env"),
            TextWriter.Null,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(runner.Requests);
        var request = Assert.Single(runner.DockerActionRequests);
        Assert.Equal("alpine:3.20", request.Image);
        Assert.Equal("/bin/echo", request.EntryPoint);
        Assert.Equal(["hello world", "--count", "2"], request.Arguments);
        Assert.Equal("true", request.Environment["DOTNET_NOLOGO"]);
        Assert.Equal("\"hello world\" --count 2", request.Environment["INPUT_ARGS"]);
        Assert.Equal("/bin/echo", request.Environment["INPUT_ENTRYPOINT"]);
        Assert.Equal("22", request.Environment["INPUT_NODE_VERSION"]);
        AssertDefaultEnvironment(
            request.Environment,
            "run-docker-env",
            "CI",
            "test",
            "step_1",
            "Use image",
            "workflow_dispatch",
            "CLI",
            expectedCi: "true");
        Assert.Equal("alpine:3.20", Assert.Single(cache.DockerImageRequests).Image);
    }

    [Fact]
    public async Task ExecuteAsync_FailsDockerImageActionWhenArgsCannotBeParsed()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
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
                            ["args"] = "\"unterminated"
                        })
                ]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.DockerActionRequests);
        Assert.Contains(result.Errors, error => error.Contains("with.args", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("unterminated quote", StringComparison.OrdinalIgnoreCase));
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
            var actionMount = Assert.Single(request.AdditionalMounts, mount => mount.ContainerPath == "/actio/action");
            Assert.Equal(actionRoot, actionMount.HostPath);
            Assert.True(actionMount.ReadOnly);
            var environmentFileMount = Assert.Single(request.AdditionalMounts, mount => mount.ContainerPath == "/actio/env");
            Assert.False(environmentFileMount.ReadOnly);
        }
        finally
        {
            Directory.Delete(actionRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsGitHubDockerfileAction()
    {
        var actionRoot = Path.Combine(Path.GetTempPath(), $"actio-github-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(actionRoot);
        var actionPath = Path.Combine(actionRoot, "action.yml");
        var dockerfilePath = Path.Combine(actionRoot, "Dockerfile");
        var commitSha = new string('c', 40);
        await File.WriteAllTextAsync(
            actionPath,
            """
            name: Remote Dockerfile action
            inputs:
              name:
                required: true
            runs:
              using: docker
              image: Dockerfile
            """);
        await File.WriteAllTextAsync(
            dockerfilePath,
            """
            FROM alpine:3.20
            CMD ["sh", "-c", "echo remote"]
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
                        "github-key",
                        "github",
                        "owner/repo/action@v1",
                        actionPath,
                        commitSha,
                        actionRoot,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        commitSha,
                        "v1"))
            };
            var workflow = CreateWorkflow(
                new WorkflowJob(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [
                        new WorkflowStep(
                            "Use GitHub Dockerfile action",
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
            Assert.Empty(runner.Requests);
            Assert.Empty(runner.DockerActionRequests);
            Assert.Single(cache.GitHubSourceRequests);
            var cacheRequest = Assert.Single(cache.DockerfileRequests);
            Assert.Equal("owner/repo/action@v1", cacheRequest.Uses);
            Assert.Equal(actionRoot, cacheRequest.ActionDirectory);
            Assert.Equal(dockerfilePath, cacheRequest.DockerfilePath);
            Assert.Equal(commitSha, cacheRequest.PinnedIdentity);
            Assert.Equal("v1", cacheRequest.MutablePart);

            var request = Assert.Single(runner.DockerfileActionRequests);
            Assert.Equal(actionRoot, request.BuildContext);
            Assert.Equal(dockerfilePath, request.DockerfilePath);
            Assert.Equal("Remote", request.Environment["INPUT_NAME"]);
            var actionMount = Assert.Single(request.AdditionalMounts, mount => mount.ContainerPath == "/actio/action");
            Assert.Equal(actionRoot, actionMount.HostPath);
            Assert.True(actionMount.ReadOnly);
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
    public async Task ExecuteAsync_RunsGitHubJavaScriptAction()
    {
        var actionRoot = Path.Combine(Path.GetTempPath(), $"actio-github-action-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(actionRoot, "dist"));
        var actionPath = Path.Combine(actionRoot, "action.yml");
        await File.WriteAllTextAsync(
            actionPath,
            """
            name: JavaScript action
            inputs:
              name:
                default: Actio
            runs:
              using: node20
              main: dist/index.js
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
                    [
                        new WorkflowStep(
                            "Use setup-node",
                            null,
                            "actions/setup-node@v4",
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
            Assert.Empty(runner.Requests);
            Assert.Empty(runner.DockerActionRequests);
            var request = Assert.Single(runner.JavaScriptActionRequests);
            Assert.Equal("/actio/action", request.ActionPath);
            Assert.Equal("dist/index.js", request.Main);
            Assert.Null(request.Pre);
            Assert.Null(request.Post);
            Assert.Equal("Remote", request.Environment["INPUT_NAME"]);
            var actionMount = Assert.Single(request.AdditionalMounts, mount => mount.ContainerPath == "/actio/action");
            Assert.Equal(actionRoot, actionMount.HostPath);
            Assert.True(actionMount.ReadOnly);
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

    [Fact]
    public async Task ExecuteAsync_CallsLocalReusableWorkflowAndExposesOutputs()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-reusable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".workflows"));
        File.WriteAllText(
            Path.Combine(projectRoot, ".workflows", "reusable-build.yml"),
            """
            name: Reusable Build
            on:
              workflow_call:
                inputs:
                  configuration:
                    required: true
                    type: string
                secrets:
                  token:
                    required: true
                outputs:
                  package-path:
                    value: "${{ jobs.build.outputs.package-path }}"
            jobs:
              build:
                if: "${{ secrets.token != '' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        try
        {
            var runner = new FakeRunnerProvider(
                [
                    new FakeRunnerStep(0, ["secret is super-token", "actio.output package-path=dist/Release.zip"]),
                    new FakeRunnerStep(0)
                ]);
            var workflow = CreateWorkflow(
                CreateReusableWorkflowCallJob(
                    "build",
                    "./.workflows/reusable-build.yml",
                    new Dictionary<string, string> { ["configuration"] = "${{ vars.CONFIGURATION }}" },
                    new Dictionary<string, string> { ["token"] = "${{ secrets.NUGET_TOKEN }}" }),
                new WorkflowJob(
                    "consume",
                    ["build"],
                    "${{ needs.build.outputs.package-path == 'dist/Release.zip' }}",
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Consume package", "echo consume", null)]));

            using var output = new StringWriter();
            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(
                    projectRoot,
                    Path.Combine(projectRoot, ".workflows", "caller.yml"),
                    "run-reusable",
                    Secrets: new Dictionary<string, string> { ["NUGET_TOKEN"] = "super-token" },
                    Variables: new Dictionary<string, string> { ["CONFIGURATION"] = "Release" }),
                output,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(2, result.SuccessfulSteps);
            Assert.Equal(2, result.TotalSteps);
            Assert.Equal(["build", "consume"], runner.Requests.Select(request => request.JobName));
            Assert.Contains(result.Outputs, item =>
                item.JobName == "build" &&
                item.Name == "package-path" &&
                item.Value == "dist/Release.zip");
            Assert.Contains("secret is ***", output.ToString());
            Assert.DoesNotContain("super-token", output.ToString());
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesLocalVarsAndSecretsInConditionsAndStepEnvironment()
    {
        var runner = new FakeRunnerProvider(
            [
                new FakeRunnerStep(
                    0,
                    ["token is local-secret"],
                    onExecute: (environment, _) =>
                    {
                        Assert.Equal("true", environment["ACTIO_VAR_RUN_TESTS"]);
                        Assert.Equal("local-secret", environment["ACTIO_SECRET_NUGET_TOKEN"]);
                    })
            ]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                "${{ vars.RUN_TESTS == 'true' && secrets.NUGET_TOKEN != '' }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Use local values", "echo local", null)]));

        using var output = new StringWriter();
        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(
                Environment.CurrentDirectory,
                Secrets: new Dictionary<string, string> { ["NUGET_TOKEN"] = "local-secret" },
                Variables: new Dictionary<string, string> { ["RUN_TESTS"] = "true" }),
            output,
            TextWriter.Null);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Single(runner.Requests);
        Assert.Contains("token is ***", output.ToString());
        Assert.DoesNotContain("local-secret", output.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_FailsClearlyWhenReferencedSecretIsMissing()
    {
        var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
        var workflow = CreateWorkflow(
            new WorkflowJob(
                "test",
                [],
                "${{ secrets.NUGET_TOKEN != '' }}",
                "ubuntu-latest",
                new Dictionary<string, string>(),
                [new WorkflowStep("Use secret", "echo local", null)]));

        var result = await new WorkflowExecutor(runner).ExecuteAsync(
            workflow,
            new WorkflowExecutionOptions(Environment.CurrentDirectory),
            TextWriter.Null,
            TextWriter.Null);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Errors, error => error.Contains("secrets.NUGET_TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_FailsReusableWorkflowCallWhenRequiredInputIsMissing()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-reusable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".workflows"));
        File.WriteAllText(
            Path.Combine(projectRoot, ".workflows", "reusable-build.yml"),
            """
            name: Reusable Build
            on:
              workflow_call:
                inputs:
                  configuration:
                    required: true
                    type: string
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var workflow = CreateWorkflow(
                CreateReusableWorkflowCallJob(
                    "build",
                    "./.workflows/reusable-build.yml",
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>()));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Contains(result.Errors, error =>
                error.Contains("workflow.jobs.build.uses './.workflows/reusable-build.yml' -> callee", StringComparison.Ordinal) &&
                error.Contains("requires workflow_call input 'configuration'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllowsMissingOptionalReusableWorkflowSecret()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-reusable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".workflows"));
        File.WriteAllText(
            Path.Combine(projectRoot, ".workflows", "optional-secret.yml"),
            """
            name: Optional Secret
            on:
              workflow_call:
                secrets:
                  token:
                    required: false
            jobs:
              check:
                if: "${{ secrets.token == '' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Check
                    run: echo optional
            """);

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var workflow = CreateWorkflow(
                CreateReusableWorkflowCallJob(
                    "check_secret",
                    "./.workflows/optional-secret.yml",
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>()));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Single(runner.Requests);
            Assert.Equal("check", runner.Requests[0].JobName);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsReusableWorkflowOutsideWorkflowFolders()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"actio-reusable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "reusable.yml"), "name: Reusable");

        try
        {
            var runner = new FakeRunnerProvider([new FakeRunnerStep(0)]);
            var workflow = CreateWorkflow(
                CreateReusableWorkflowCallJob(
                    "build",
                    "./reusable.yml",
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>()));

            var result = await new WorkflowExecutor(runner).ExecuteAsync(
                workflow,
                new WorkflowExecutionOptions(projectRoot),
                TextWriter.Null,
                TextWriter.Null);

            Assert.False(result.Success);
            Assert.Empty(runner.Requests);
            Assert.Contains(result.Errors, error => error.Contains("must reference a workflow under .workflows/ or .github/workflows/", StringComparison.Ordinal));
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

    private static WorkflowJob CreateReusableWorkflowCallJob(
        string name,
        string uses,
        IReadOnlyDictionary<string, string> with,
        IReadOnlyDictionary<string, string> secrets)
    {
        return new WorkflowJob(
            name,
            null,
            [],
            null,
            "reusable-workflow",
            new Dictionary<string, string>(),
            WorkflowRunDefaults.Empty,
            null,
            false,
            null,
            WorkflowJobStrategy.Empty,
            new Dictionary<string, string>(),
            [],
            [],
            call: new WorkflowJobCall(uses, with, secrets));
    }

    private static WorkflowJob CreateMatrixJob(
        string name,
        IReadOnlyList<string> needs,
        string runsOn,
        IReadOnlyList<WorkflowStep> steps,
        WorkflowJobStrategy? strategy = null)
    {
        return new WorkflowJob(
            name,
            null,
            needs,
            null,
            runsOn,
            new Dictionary<string, string>(),
            WorkflowRunDefaults.Empty,
            null,
            false,
            null,
            strategy ?? CreateMatrixStrategy(["ubuntu-latest", "debian-latest"]),
            new Dictionary<string, string>(),
            [],
            steps);
    }

    private static WorkflowJobStrategy CreateMatrixStrategy(
        IReadOnlyList<string> osValues,
        bool failFast = true,
        int? maxParallel = null)
    {
        return new WorkflowJobStrategy(
            new WorkflowJobMatrix(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["os"] = osValues,
                    ["dotnet"] = ["10.0"]
                }),
            failFast,
            maxParallel);
    }

    private static void AssertDefaultEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string runId,
        string workflowName,
        string jobName,
        string stepIdentity,
        string stepName,
        string eventName,
        string eventSource,
        string expectedCi)
    {
        var actor = string.IsNullOrWhiteSpace(Environment.UserName)
            ? "local"
            : Environment.UserName;

        Assert.Equal("true", environment["ACTIO"]);
        Assert.Equal(eventName, environment["ACTIO_EVENT_NAME"]);
        Assert.Equal(eventSource, environment["ACTIO_EVENT_SOURCE"]);
        Assert.Equal(jobName, environment["ACTIO_JOB"]);
        Assert.Equal(runId, environment["ACTIO_RUN_ID"]);
        Assert.Equal(stepIdentity, environment["ACTIO_STEP"]);
        Assert.Equal(stepName, environment["ACTIO_STEP_NAME"]);
        Assert.Equal(workflowName, environment["ACTIO_WORKFLOW"]);
        Assert.Equal("/workspace", environment["ACTIO_WORKSPACE"]);
        Assert.Equal(expectedCi, environment["CI"]);
        Assert.Equal(stepIdentity, environment["GITHUB_ACTION"]);
        Assert.Equal("true", environment["GITHUB_ACTIONS"]);
        Assert.Equal(actor, environment["GITHUB_ACTOR"]);
        Assert.Equal(eventName, environment["GITHUB_EVENT_NAME"]);
        Assert.Equal(jobName, environment["GITHUB_JOB"]);
        Assert.Equal("1", environment["GITHUB_RUN_ATTEMPT"]);
        Assert.Equal(runId, environment["GITHUB_RUN_ID"]);
        Assert.Equal(actor, environment["GITHUB_TRIGGERING_ACTOR"]);
        Assert.Equal(workflowName, environment["GITHUB_WORKFLOW"]);
        Assert.Equal("/workspace", environment["GITHUB_WORKSPACE"]);
        Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
        Assert.False(string.IsNullOrWhiteSpace(environment["RUNNER_ARCH"]));
        Assert.Equal("docker", environment["RUNNER_ENVIRONMENT"]);
        Assert.Equal("ubuntu-latest", environment["RUNNER_NAME"]);
        Assert.Equal("Linux", environment["RUNNER_OS"]);
    }

    private static void WriteEnvironmentFile(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> mounts,
        string variableName,
        string content)
    {
        File.AppendAllText(ResolveMountedPath(environment[variableName], mounts), content);
    }

    private static string ResolveMountedPath(string containerPath, IReadOnlyList<StepExecutionMount> mounts)
    {
        var normalizedContainerPath = containerPath.Replace('\\', '/');
        var mount = mounts.First(item =>
            normalizedContainerPath.StartsWith(item.ContainerPath.TrimEnd('/') + "/", StringComparison.Ordinal));
        var relativePath = normalizedContainerPath[(mount.ContainerPath.TrimEnd('/').Length + 1)..]
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(mount.HostPath, relativePath);
    }

    private sealed class FakeRunnerProvider : IRunnerProvider
    {
        private readonly object _gate = new();
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

        public List<DockerfileActionExecutionRequest> DockerfileActionRequests { get; } = [];

        public List<JavaScriptActionExecutionRequest> JavaScriptActionRequests { get; } = [];

        public List<ServiceContainerStartRequest> ServiceStartRequests { get; } = [];

        public List<JobServiceNetwork> StoppedServiceNetworks { get; } = [];

        public ServiceContainerStartResult? ServiceStartResult { get; set; }

        public bool SupportsRunner(string runsOn)
        {
            return _supportsRunner;
        }

        public Task<ServiceContainerStartResult> StartServiceContainersAsync(
            ServiceContainerStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ServiceStartRequests.Add(request);
            return Task.FromResult(
                ServiceStartResult ??
                ServiceContainerStartResult.Started(new JobServiceNetwork(
                    "actio-test-network",
                    request.Services.Select(service => $"actio-{service.Name}").ToArray())));
        }

        public Task<ServiceContainerStopResult> StopServiceContainersAsync(
            JobServiceNetwork network,
            CancellationToken cancellationToken = default)
        {
            StoppedServiceNetworks.Add(network);
            return Task.FromResult(new ServiceContainerStopResult([]));
        }

        public Task<StepExecutionResult> ExecuteStepAsync(
            StepExecutionRequest request,
            IStepOutputSink output,
            CancellationToken cancellationToken = default)
        {
            FakeRunnerStep step;
            lock (_gate)
            {
                step = _steps.Dequeue();
                Requests.Add(request);
            }

            return ExecuteStepAsync(step, request.Environment, request.AdditionalMounts, output, cancellationToken);
        }

        public Task<StepExecutionResult> ExecuteDockerActionAsync(
            DockerActionExecutionRequest request,
            IStepOutputSink output,
            CancellationToken cancellationToken = default)
        {
            FakeRunnerStep step;
            lock (_gate)
            {
                step = _steps.Dequeue();
                DockerActionRequests.Add(request);
            }

            return ExecuteStepAsync(step, request.Environment, request.AdditionalMounts, output, cancellationToken);
        }

        public Task<StepExecutionResult> ExecuteDockerfileActionAsync(
            DockerfileActionExecutionRequest request,
            IStepOutputSink output,
            CancellationToken cancellationToken = default)
        {
            FakeRunnerStep step;
            lock (_gate)
            {
                step = _steps.Dequeue();
                DockerfileActionRequests.Add(request);
            }

            return ExecuteStepAsync(step, request.Environment, request.AdditionalMounts, output, cancellationToken);
        }

        public Task<StepExecutionResult> ExecuteJavaScriptActionAsync(
            JavaScriptActionExecutionRequest request,
            IStepOutputSink output,
            CancellationToken cancellationToken = default)
        {
            FakeRunnerStep step;
            lock (_gate)
            {
                step = _steps.Dequeue();
                JavaScriptActionRequests.Add(request);
            }

            return ExecuteStepAsync(step, request.Environment, request.AdditionalMounts, output, cancellationToken);
        }

        private static async Task<StepExecutionResult> ExecuteStepAsync(
            FakeRunnerStep step,
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyList<StepExecutionMount> mounts,
            IStepOutputSink output,
            CancellationToken cancellationToken)
        {
            step.OnExecute?.Invoke(environment, mounts);

            try
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
            finally
            {
                step.OnComplete?.Invoke();
            }
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

        public Task<StepEnvironmentFiles> CreateStepEnvironmentFilesAsync(
            string runId,
            string jobName,
            int stepIndex,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateStepEnvironmentFiles(runId, jobName, stepIndex, stepName));
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

        public List<string> LogLines { get; } = [];

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
            return Task.FromResult<IStepLog>(new RecordingStepLog(LogLines));
        }

        public Task<StepEnvironmentFiles> CreateStepEnvironmentFilesAsync(
            string runId,
            string jobName,
            int stepIndex,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateStepEnvironmentFiles(runId, jobName, stepIndex, stepName));
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

    private sealed class RecordingStepLog : IStepLog
    {
        private readonly List<string> _lines;

        public RecordingStepLog(List<string> lines)
        {
            _lines = lines;
        }

        public string? LogPath => null;

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _lines.Add($"[stdout] {line}");
            return Task.CompletedTask;
        }

        public Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _lines.Add($"[stderr] {line}");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingActionCache : IActionCache, IGitHubActionSourceProvider
    {
        public List<LocalActionCacheRequest> Requests { get; } = [];

        public List<DockerImageActionCacheRequest> DockerImageRequests { get; } = [];

        public List<DockerfileActionCacheRequest> DockerfileRequests { get; } = [];

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

        public Task<ActionCacheEntry> GetOrAddDockerfileActionAsync(
            DockerfileActionCacheRequest request,
            CancellationToken cancellationToken = default)
        {
            DockerfileRequests.Add(request);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ActionCacheEntry(
                request.ContentHash,
                "dockerfile",
                request.Uses,
                request.DockerfilePath,
                request.ContentHash,
                string.Empty,
                now,
                now,
                request.PinnedIdentity,
                request.MutablePart));
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
            TimeSpan? delay = null,
            Action<IReadOnlyDictionary<string, string>, IReadOnlyList<StepExecutionMount>>? onExecute = null,
            Action? onComplete = null)
        {
            ExitCode = exitCode;
            OutputLines = outputLines ?? [];
            ErrorLines = errorLines ?? [];
            Delay = delay ?? TimeSpan.Zero;
            OnExecute = onExecute;
            OnComplete = onComplete;
        }

        public int ExitCode { get; }

        public IReadOnlyList<string> OutputLines { get; }

        public IReadOnlyList<string> ErrorLines { get; }

        public TimeSpan Delay { get; }

        public Action<IReadOnlyDictionary<string, string>, IReadOnlyList<StepExecutionMount>>? OnExecute { get; }

        public Action? OnComplete { get; }
    }

    private static StepEnvironmentFiles CreateStepEnvironmentFiles(
        string runId,
        string jobName,
        int stepIndex,
        string stepName)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "actio-engine-tests",
            SanitizePathSegment(runId),
            SanitizePathSegment(jobName),
            $"{stepIndex + 1:D3}-{SanitizePathSegment(stepName)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var files = new StepEnvironmentFiles(
            directory,
            Path.Combine(directory, StepEnvironmentFiles.EnvironmentFileName),
            Path.Combine(directory, StepEnvironmentFiles.OutputFileName),
            Path.Combine(directory, StepEnvironmentFiles.PathFileName),
            Path.Combine(directory, StepEnvironmentFiles.StepSummaryFileName),
            Path.Combine(directory, StepEnvironmentFiles.StateFileName));

        File.WriteAllText(files.EnvironmentFilePath, string.Empty);
        File.WriteAllText(files.OutputFilePath, string.Empty);
        File.WriteAllText(files.PathFilePath, string.Empty);
        File.WriteAllText(files.StepSummaryFilePath, string.Empty);
        File.WriteAllText(files.StateFilePath, string.Empty);
        return files;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidChars.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }
}
