using Actio.Core.Workflows;
using Actio.Core.Security;
using Actio.Engine.Actions;
using Actio.Engine.Caching;
using Actio.Engine.Execution;
using Actio.Engine.Runs;
using Actio.Storage;

namespace Actio.Web.Tests;

public sealed class ActioWebDataServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-web-tests-{Guid.NewGuid():N}");
    private readonly string _projectRoot;
    private readonly string _actioHome;

    public ActioWebDataServiceTests()
    {
        _projectRoot = Path.Combine(_root, "repo");
        _actioHome = Path.Combine(_root, "actio-home");
        Directory.CreateDirectory(Path.Combine(_projectRoot, ".workflows"));
    }

    [Fact]
    public async Task GetWorkflowsAsync_ReturnsWorkflowWithLatestRun()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var workflows = await CreateService().GetWorkflowsAsync();

        var workflow = Assert.Single(workflows);
        Assert.Equal("CI", workflow.Name);
        Assert.Equal("ci.yml", workflow.FileName);
        Assert.Equal("run-1", workflow.LatestRunId);
        Assert.Equal(1, workflow.RunCount);
    }

    [Fact]
    public async Task GetWorkflowsAsync_ReturnsGitHubWorkflowWhenActioWorkflowIsMissing()
    {
        var workflowPath = WriteGitHubWorkflow("ci.yml", "GitHub CI");
        await SaveRunAsync(CreateRun("run-1", "GitHub CI", workflowPath));

        var workflows = await CreateService().GetWorkflowsAsync();

        var workflow = Assert.Single(workflows);
        Assert.Equal("GitHub CI", workflow.Name);
        Assert.Equal(workflowPath, workflow.Path);
        Assert.Equal("run-1", workflow.LatestRunId);
    }

    [Fact]
    public async Task GetWorkflowsAsync_PrefersActioWorkflowWhenBothRootsContainSameFilename()
    {
        var actioWorkflowPath = WriteWorkflow("ci.yml", "Actio CI");
        WriteGitHubWorkflow("ci.yml", "GitHub CI");

        var workflows = await CreateService().GetWorkflowsAsync();

        var workflow = Assert.Single(workflows);
        Assert.Equal("Actio CI", workflow.Name);
        Assert.Equal(actioWorkflowPath, workflow.Path);
    }

    [Fact]
    public async Task GetRunsAsync_ReturnsOnlyRunsForProjectRoot()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));
        await SaveRunAsync(CreateRun("run-other", "Other", workflowPath, projectRoot: Path.Combine(_root, "other")));

        var runs = await CreateService().GetRunsAsync();

        var run = Assert.Single(runs);
        Assert.Equal("run-1", run.RunId);
        Assert.Equal("workflow_dispatch (CLI)", run.Trigger);
    }

    [Fact]
    public async Task GetRunsAsync_ReturnsRunTriggerSource()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            runTrigger: new WorkflowRunTrigger(
                "repository_dispatch",
                "Local API",
                new Dictionary<string, string> { ["event_type"] = "deploy" })));

        var run = Assert.Single(await CreateService().GetRunsAsync());

        Assert.Equal("repository_dispatch (Local API)", run.Trigger);

        var detail = await CreateService().GetRunAsync("run-1");
        Assert.NotNull(detail);
        Assert.Equal("repository_dispatch", detail.RunTrigger.EventPayload.EventName);
        Assert.Equal("Local API", detail.RunTrigger.EventPayload.Source);
        Assert.Equal("deploy", detail.RunTrigger.EventPayload.Inputs["event_type"]);
    }

    [Fact]
    public async Task GetRunsAsync_RefreshesRunningDuration()
    {
        var startedAt = DateTimeOffset.Parse("2026-06-24T10:00:00Z");
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            status: "Running",
            startedAt: startedAt,
            finishedAt: startedAt,
            durationMilliseconds: 0));

        var service = CreateService(new FixedTimeProvider(startedAt.AddSeconds(7)));

        var run = Assert.Single(await service.GetRunsAsync());

        Assert.Equal("Running", run.Status);
        Assert.Equal(7000, run.DurationMilliseconds);
    }

    [Fact]
    public async Task GetRunAsync_RefreshesRunningDuration()
    {
        var startedAt = DateTimeOffset.Parse("2026-06-24T10:00:00Z");
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            status: "Running",
            startedAt: startedAt,
            finishedAt: startedAt,
            durationMilliseconds: 0));

        var service = CreateService(new FixedTimeProvider(startedAt.AddSeconds(9)));

        var run = await service.GetRunAsync("run-1");

        Assert.NotNull(run);
        Assert.Equal("Running", run.Status);
        Assert.Equal(9000, run.DurationMilliseconds);
    }

    [Fact]
    public async Task GetRunAsync_ReturnsJobEnvironmentMetadata()
    {
        var workflowPath = WriteWorkflow("deploy.yml", "Deploy");
        await SaveRunAsync(CreateRun(
            "run-1",
            "Deploy",
            workflowPath,
            environment: new WorkflowJobEnvironment("production", "https://actio.local/deployments/42")));

        var run = await CreateService().GetRunAsync("run-1");

        Assert.NotNull(run);
        var environment = Assert.Single(run.Jobs).Environment;
        Assert.NotNull(environment);
        Assert.Equal("production", environment.Name);
        Assert.Equal("https://actio.local/deployments/42", environment.Url);
    }

    [Fact]
    public async Task GetRunAsync_ReturnsSecurityFindings()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            securityFindings:
            [
                new WorkflowSecurityFinding(
                    "warning",
                    "external-action.mutable-ref",
                    "workflow.jobs.test.steps[0].uses",
                    "External action 'docker://node:22' uses mutable identity '22'.",
                    "Pin Docker image actions with a sha256 digest for safer reuse.")
            ]));

        var run = await CreateService().GetRunAsync("run-1");

        Assert.NotNull(run);
        var finding = Assert.Single(run.SecurityFindings);
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("external-action.mutable-ref", finding.Category);
        Assert.Equal("workflow.jobs.test.steps[0].uses", finding.Location);
    }

    [Fact]
    public async Task GetRunAsync_ReturnsRunnerSecurityMetadata()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            runnerSecurity: new RunnerSecurityMetadata(
                "docker",
                "secure-baseline",
                "secure-baseline",
                ["no-new-privileges=true"],
                "docker-default-no-additions",
                "docker-default-seccomp-and-lsm-preserved",
                "not-evaluated",
                ["daemon-platform-security-not-evaluated"],
                "image-default-user-with-root-warning",
                "writable",
                "read-write-with-protected-value-file-masks",
                "canonical-existing-bind-sources-only",
                ["/workspace/.actio/secrets.env"],
                [new RunnerImageUserObservation("shell:test", "alpine:3.20", "<image-default-root>", "root")],
                "per-job-user-defined-bridge-with-outbound",
                "ipv4-loopback-only",
                [
                    new RunnerNetworkObservation(
                        "test",
                        "actio-test-network",
                        "user-defined-bridge",
                        OutboundAllowed: true,
                        Internal: false,
                        ["postgres"],
                        [new RunnerPublishedPort("service:postgres", "127.0.0.1", 5432, 15432, "tcp")])
                ],
                EffectiveResourceLimits: ContainerResourceLimits.Defaults,
                Preflight: new RunnerPreflightEvidence(
                    Status: "passed",
                    EngineVersion: "29.0.0",
                    CgroupVersion: "2"),
                Cleanup: new RunnerCleanupEvidence(
                    CandidateContainers: 1,
                    RemovedContainers: 1),
                StrictControls: ["cap-drop-all"],
                JavaScriptRuntimeObservations:
                [
                    new RunnerJavaScriptRuntimeObservation(
                        "javascript-action:test/action/main",
                        "node24",
                        "actio/javascript-action:node24-example",
                        "node:24.18.0-bookworm-slim@sha256:example",
                        "definition-hash",
                        "24.18.0",
                        "1:2.39.5-0+deb12u3",
                        "20230311+deb12u1")
                ])));

        var run = await CreateService().GetRunAsync("run-1");

        Assert.NotNull(run);
        Assert.NotNull(run.RunnerSecurity);
        Assert.Equal("docker", run.RunnerSecurity.Provider);
        Assert.Equal("secure-baseline", run.RunnerSecurity.EffectiveProfile);
        Assert.Contains("no-new-privileges=true", run.RunnerSecurity.AppliedSecurityOptions);
        Assert.Equal("not-evaluated", run.RunnerSecurity.DaemonPlatformState);
        Assert.Equal("ipv4-loopback-only", run.RunnerSecurity.PublishedPortPolicy);
        Assert.Equal("actio-test-network", Assert.Single(run.RunnerSecurity.NetworkObservations).NetworkName);
        Assert.Equal("image-default-user-with-root-warning", run.RunnerSecurity.UserPolicy);
        Assert.Equal("writable", run.RunnerSecurity.RootFilesystemPolicy);
        Assert.Contains("/workspace/.actio/secrets.env", run.RunnerSecurity.ProtectedPaths);
        Assert.Equal("root", Assert.Single(run.RunnerSecurity.ImageUserObservations).Status);
        Assert.Equal(2, run.RunnerSecurity.EffectiveResourceLimits?.Cpu);
        Assert.Equal("passed", run.RunnerSecurity.Preflight?.Status);
        Assert.Equal(1, run.RunnerSecurity.Cleanup?.RemovedContainers);
        Assert.Contains("cap-drop-all", run.RunnerSecurity.StrictControls);
        Assert.Equal("node24", Assert.Single(run.RunnerSecurity.JavaScriptRuntimeObservations).Runtime);
    }

    [Fact]
    public async Task GetStepLogAsync_ReturnsLogContent()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var logPath = Path.Combine(_actioHome, "logs", "run-1", "test", "001-Test.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, "hello log");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath, logPath: logPath));

        var log = await CreateService().GetStepLogAsync("run-1", "test", "Test");

        Assert.NotNull(log);
        Assert.Equal("hello log", log.Content);
    }

    [Fact]
    public async Task GetStepLogAsync_CanResolveJobByIdWhenDisplayNameDiffers()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var logPath = Path.Combine(_actioHome, "logs", "run-1", "test", "001-Test.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, "hello display log");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            logPath: logPath,
            jobName: "Run tests",
            jobId: "test"));

        var log = await CreateService().GetStepLogAsync("run-1", "test", "Test");

        Assert.NotNull(log);
        Assert.Equal("hello display log", log.Content);
    }

    [Fact]
    public async Task GetStepLogAsync_CanResolveStepById()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var logPath = Path.Combine(_actioHome, "logs", "run-1", "test", "001-Test.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, "hello step id log");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath, logPath: logPath, stepId: "run_tests"));

        var log = await CreateService().GetStepLogAsync("run-1", "test", "run_tests");

        Assert.NotNull(log);
        Assert.Equal("hello step id log", log.Content);
    }

    [Fact]
    public async Task GetRunAsync_ReturnsStepSummary()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath, stepSummary: "### Summary\nAll good\n"));

        var run = await CreateService().GetRunAsync("run-1");

        Assert.NotNull(run);
        var step = Assert.Single(Assert.Single(run.Jobs).Steps);
        Assert.Equal("### Summary\nAll good\n", step.Summary);
    }

    [Fact]
    public async Task GetRunAsync_ReturnsStepAnnotations()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-1",
            "CI",
            workflowPath,
            annotations:
            [
                new StepLogAnnotation(
                    "warning",
                    "be careful",
                    "Careful",
                    "src/app.cs",
                    12)
            ]));

        var run = await CreateService().GetRunAsync("run-1");

        Assert.NotNull(run);
        var annotation = Assert.Single(Assert.Single(Assert.Single(run.Jobs).Steps).Annotations);
        Assert.Equal("warning", annotation.Level);
        Assert.Equal("be careful", annotation.Message);
        Assert.Equal("Careful", annotation.Title);
        Assert.Equal("src/app.cs", annotation.File);
        Assert.Equal(12, annotation.Line);
    }

    [Fact]
    public async Task GetArtifactAsync_ReturnsFileArtifact()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var artifactPath = Path.Combine(_actioHome, "artifacts", "run-1", "test", "report", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllTextAsync(artifactPath, "report");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath, artifactPath: artifactPath));

        var artifact = await CreateService().GetArtifactAsync("run-1", "test", "report");

        Assert.NotNull(artifact);
        Assert.True(artifact.IsFile);
        Assert.Equal("text/plain", artifact.ContentType);
    }

    [Fact]
    public async Task GetWorkflowFileAsync_ReturnsWorkflowYaml()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var content = await CreateService().GetWorkflowFileAsync("run-1");

        Assert.NotNull(content);
        Assert.Contains("name: CI", content);
    }

    [Fact]
    public async Task GetWorkflowFileResultAsync_ReturnsFileNameAndContent()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var result = await CreateService().GetWorkflowFileResultAsync("run-1");

        Assert.NotNull(result);
        Assert.Equal("ci.yml", result.FileName);
        Assert.Contains("name: CI", result.Content);
    }

    [Fact]
    public async Task UpdateWorkflowFileAsync_SavesValidWorkflow()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var result = await CreateService().UpdateWorkflowFileAsync(
            "run-1",
            """
            name: Updated CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains("name: Updated CI", await File.ReadAllTextAsync(workflowPath));
    }

    [Fact]
    public async Task UpdateWorkflowFileAsync_SavesGitHubWorkflow()
    {
        var workflowPath = WriteGitHubWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var result = await CreateService().UpdateWorkflowFileAsync(
            "run-1",
            """
            name: Updated CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains("name: Updated CI", await File.ReadAllTextAsync(workflowPath));
    }

    [Fact]
    public async Task UpdateWorkflowFileAsync_RejectsInvalidWorkflowWithoutOverwriting()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        var original = await File.ReadAllTextAsync(workflowPath);
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var result = await CreateService().UpdateWorkflowFileAsync("run-1", "name: Broken");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("workflow.jobs is required", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(original, await File.ReadAllTextAsync(workflowPath));
    }

    [Fact]
    public async Task UpdateWorkflowFileAsync_RejectsWorkflowOutsideWorkflowsDirectory()
    {
        var workflowPath = Path.Combine(_projectRoot, "ci.yml");
        await File.WriteAllTextAsync(workflowPath, "name: CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));

        var result = await CreateService().UpdateWorkflowFileAsync(
            "run-1",
            """
            name: Updated CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(".workflows", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains(".github/workflows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetCacheAsync_ReturnsActionAndDependencyCacheEntries()
    {
        var cache = new FileSystemActionCache(_actioHome);
        await cache.GetOrAddDockerImageActionAsync(
            new DockerImageActionCacheRequest("docker://hello-world:latest", "hello-world:latest", false, "latest"));
        var dependencyCache = new FileSystemDependencyCache(_actioHome);
        var packagePath = Path.Combine(_projectRoot, ".nuget", "packages");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");
        await dependencyCache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "nuget-main", [".nuget/packages"]));

        var result = await CreateService().GetCacheAsync();

        var entry = Assert.Single(result.Entries);
        Assert.Equal("docker", entry.Kind);
        Assert.Equal("docker://hello-world:latest", entry.Uses);
        var dependencyEntry = Assert.Single(result.DependencyEntries);
        Assert.Equal("nuget-main", dependencyEntry.Key);
        Assert.Contains(Path.Combine("cache"), result.CacheRoot);
        Assert.Contains(Path.Combine("cache", "dependencies"), result.DependencyCacheRoot);
    }

    [Fact]
    public async Task CleanCacheAsync_RemovesActionAndDependencyCacheEntries()
    {
        var cache = new FileSystemActionCache(_actioHome);
        await cache.GetOrAddDockerImageActionAsync(
            new DockerImageActionCacheRequest("docker://hello-world:latest", "hello-world:latest", false, "latest"));
        var dependencyCache = new FileSystemDependencyCache(_actioHome);
        var packagePath = Path.Combine(_projectRoot, ".nuget", "packages");
        Directory.CreateDirectory(packagePath);
        await File.WriteAllTextAsync(Path.Combine(packagePath, "package.txt"), "cached");
        await dependencyCache.SaveAsync(new DependencyCacheSaveRequest(_projectRoot, "nuget-main", [".nuget/packages"]));

        var result = await CreateService().CleanCacheAsync();

        Assert.Equal(2, result.Removed);
        Assert.Empty((await cache.ListAsync()));
        Assert.Empty((await dependencyCache.ListAsync()));
    }

    [Fact]
    public async Task GetRunAsync_ReturnsNullForCorruptedRunRecord()
    {
        var runDirectory = Path.Combine(_actioHome, "runs", "run-bad");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run.json"), "not json");

        var run = await CreateService().GetRunAsync("run-bad");

        Assert.Null(run);
    }

    [Fact]
    public async Task ProjectScopedOperationsRejectRunFromDifferentProject()
    {
        var foreignProject = Path.Combine(_root, "foreign-project");
        var foreignWorkflowDirectory = Path.Combine(foreignProject, ".workflows");
        Directory.CreateDirectory(foreignWorkflowDirectory);
        var workflowPath = Path.Combine(foreignWorkflowDirectory, "ci.yml");
        await File.WriteAllTextAsync(
            workflowPath,
            """
            name: Foreign
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);
        var logPath = Path.Combine(_actioHome, "logs", "foreign-run", "test", "001-Test.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, "foreign log");
        var artifactPath = Path.Combine(
            _actioHome,
            "artifacts",
            "foreign-run",
            "test",
            "report",
            "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllTextAsync(artifactPath, "foreign artifact");
        await SaveRunAsync(CreateRun(
            "foreign-run",
            "Foreign",
            workflowPath,
            projectRoot: foreignProject,
            logPath: logPath,
            artifactPath: artifactPath,
            status: "Running"));
        var service = CreateService();

        Assert.Null(await service.GetRunAsync("foreign-run"));
        Assert.Null(await service.GetStepLogAsync("foreign-run", "test", "Test"));
        Assert.Null(await service.GetArtifactAsync("foreign-run", "test", "report"));
        Assert.Null(await service.GetWorkflowFileResultAsync("foreign-run"));
        Assert.False((await service.CancelRunAsync("foreign-run")).Success);
        Assert.False((await service.RerunAsync("foreign-run")).Success);
        Assert.False((await service.UpdateWorkflowFileAsync(
            "foreign-run",
            "name: Changed")).Success);
        Assert.Contains(
            "name: Foreign",
            await File.ReadAllTextAsync(workflowPath));
    }

    [Fact]
    public async Task CancelRunAsync_RequestsCancellationForRunningRun()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-cancel", "CI", workflowPath, status: "Running"));

        var result = await CreateService().CancelRunAsync("run-cancel");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.True(await new FileSystemRunStore(_actioHome).IsRunCancellationRequestedAsync("run-cancel"));
    }

    [Fact]
    public async Task RerunAsync_StartsNewRunWithStoredInputs()
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
        await SaveRunAsync(CreateRun(
            "run-source",
            "CI",
            workflowPath,
            runTrigger: new WorkflowRunTrigger(
                "workflow_dispatch",
                "CLI",
                new Dictionary<string, string> { ["environment"] = "staging" })));
        var executor = new FakeWorkflowExecutor(new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var result = await CreateService(
            createExecutor: () => executor,
            scheduleBackgroundWork: work => work()).RerunAsync("run-source");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.NotNull(result.RunId);
        Assert.Equal("CI", executor.Workflow!.Name);
        Assert.Equal("rerun:run-source", executor.Options!.RunTrigger.Source);
        Assert.Equal("staging", executor.Options.RunTrigger.Inputs["environment"]);
        Assert.Equal(
            RunnerSecurityProfiles.SecureBaseline,
            executor.Options.RunnerPolicy.RequestedProfile);
    }

    [Fact]
    public async Task RerunAsync_PreservesStrictSecurityProfile()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun(
            "run-strict",
            "CI",
            workflowPath,
            runnerSecurity: new RunnerSecurityMetadata(
                "docker",
                RunnerSecurityProfiles.Strict,
                RunnerSecurityProfiles.Strict)));
        var executor = new FakeWorkflowExecutor(
            new WorkflowExecutionResult(WorkflowExecutionStatus.Success, 1, 1, []));

        var result = await CreateService(
            createExecutor: () => executor,
            scheduleBackgroundWork: work => work()).RerunAsync("run-strict");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            RunnerSecurityProfiles.Strict,
            executor.Options!.RunnerPolicy.RequestedProfile);
    }

    private ActioWebDataService CreateService(
        TimeProvider? timeProvider = null,
        Func<IWorkflowExecutor>? createExecutor = null,
        Func<Func<Task>, Task>? scheduleBackgroundWork = null)
    {
        return new ActioWebDataService(
            new ActioWebOptions(_projectRoot, _actioHome),
            new FileSystemRunStore(_actioHome),
            new FileSystemActionCache(_actioHome),
            new FileSystemDependencyCache(_actioHome),
            new Actio.Core.Workflows.WorkflowParser(),
            timeProvider,
            createExecutor,
            scheduleBackgroundWork);
    }

    private string WriteWorkflow(string fileName, string name, string? extraTopLevelYaml = null)
    {
        var path = Path.Combine(_projectRoot, ".workflows", fileName);
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

    private string WriteGitHubWorkflow(string fileName, string name)
    {
        var directory = Path.Combine(_projectRoot, ".github", "workflows");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(
            path,
            $"""
            name: {name}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);
        return path;
    }

    private async Task SaveRunAsync(WorkflowRunRecord record)
    {
        var store = new FileSystemRunStore(_actioHome);
        await store.InitializeRunAsync(record.RunId);
        await store.SaveRunRecordAsync(record);
    }

    private WorkflowRunRecord CreateRun(
        string runId,
        string workflowName,
        string workflowPath,
        string? projectRoot = null,
        string? logPath = null,
        string? artifactPath = null,
        string status = "Success",
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        long durationMilliseconds = 10,
        WorkflowRunTrigger? runTrigger = null,
        string jobName = "test",
        string? jobId = null,
        string? stepId = null,
        string? stepSummary = null,
        IReadOnlyList<StepLogAnnotation>? annotations = null,
        WorkflowJobEnvironment? environment = null,
        IReadOnlyList<WorkflowSecurityFinding>? securityFindings = null,
        RunnerSecurityMetadata? runnerSecurity = null)
    {
        var start = startedAt ?? DateTimeOffset.UtcNow;
        var finish = finishedAt ?? start;
        var artifact = artifactPath is null
            ? Array.Empty<WorkflowRunArtifact>()
            : [new WorkflowRunArtifact("test", "report", "report.txt", artifactPath)];

        return new WorkflowRunRecord(
            runId,
            workflowName,
            workflowPath,
            projectRoot ?? _projectRoot,
            status,
            start,
            finish,
            durationMilliseconds,
            [
                new JobRunRecord(
                    jobName,
                    status,
                    "ubuntu-latest",
                    [],
                    null,
                    start,
                    finish,
                    durationMilliseconds,
                    new Dictionary<string, string>(),
                    [new StepRunRecord("Test", status, "dotnet test", 0, logPath, start, finish, durationMilliseconds, stepId, Summary: stepSummary, Annotations: annotations)],
                    artifact,
                    [],
                    jobId,
                    Environment: environment)
            ],
            [],
            artifact,
            [],
            RunTrigger: runTrigger,
            SecurityFindings: securityFindings,
            RunnerSecurity: runnerSecurity);
    }

    private sealed class FakeWorkflowExecutor : IWorkflowExecutor
    {
        private readonly WorkflowExecutionResult _result;

        public FakeWorkflowExecutor(WorkflowExecutionResult result)
        {
            _result = result;
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
            return Task.FromResult(_result);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
