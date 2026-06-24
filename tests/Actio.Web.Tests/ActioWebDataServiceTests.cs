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
    public async Task GetRunsAsync_ReturnsOnlyRunsForProjectRoot()
    {
        var workflowPath = WriteWorkflow("ci.yml", "CI");
        await SaveRunAsync(CreateRun("run-1", "CI", workflowPath));
        await SaveRunAsync(CreateRun("run-other", "Other", workflowPath, projectRoot: Path.Combine(_root, "other")));

        var runs = await CreateService().GetRunsAsync();

        var run = Assert.Single(runs);
        Assert.Equal("run-1", run.RunId);
        Assert.Equal("CLI", run.Trigger);
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
    public async Task GetRunAsync_ReturnsNullForCorruptedRunRecord()
    {
        var runDirectory = Path.Combine(_actioHome, "runs", "run-bad");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run.json"), "not json");

        var run = await CreateService().GetRunAsync("run-bad");

        Assert.Null(run);
    }

    private ActioWebDataService CreateService(TimeProvider? timeProvider = null)
    {
        return new ActioWebDataService(
            new ActioWebOptions(_projectRoot, _actioHome),
            new FileSystemRunStore(_actioHome),
            new Actio.Core.Workflows.WorkflowParser(),
            timeProvider);
    }

    private string WriteWorkflow(string fileName, string name)
    {
        var path = Path.Combine(_projectRoot, ".workflows", fileName);
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
        long durationMilliseconds = 10)
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
                    "test",
                    status,
                    "ubuntu-latest",
                    [],
                    null,
                    start,
                    finish,
                    durationMilliseconds,
                    new Dictionary<string, string>(),
                    [new StepRunRecord("Test", status, "dotnet test", 0, logPath, start, finish, durationMilliseconds)],
                    artifact,
                    [])
            ],
            [],
            artifact,
            []);
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
