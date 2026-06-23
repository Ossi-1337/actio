using Actio.Core.Workflows;
using Actio.Engine.Runs;
using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class FileSystemRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-storage-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveRunRecordAsync_PersistsReadableRunJson()
    {
        var store = new FileSystemRunStore(_root);
        var runId = "run-1";
        await store.InitializeRunAsync(runId);

        var record = new WorkflowRunRecord(
            runId,
            "CI",
            "C:\\repo\\.workflows\\ci.yml",
            "C:\\repo",
            "Success",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            [],
            [],
            [],
            []);

        await store.SaveRunRecordAsync(record);
        var loaded = await store.ReadRunRecordAsync(runId);

        Assert.NotNull(loaded);
        Assert.Equal("CI", loaded.WorkflowName);
        Assert.True(File.Exists(Path.Combine(_root, "runs", runId, "run.json")));
    }

    [Fact]
    public async Task OpenStepLogAsync_WritesCapturedOutputAndErrorLines()
    {
        var store = new FileSystemRunStore(_root);
        var runId = "run-logs";
        await store.InitializeRunAsync(runId);

        await using var log = await store.OpenStepLogAsync(
            runId,
            "test",
            0,
            "Run tests");
        await log.WriteOutputLineAsync("hello");
        await log.WriteErrorLineAsync("warning");

        Assert.NotNull(log.LogPath);
        Assert.True(File.Exists(log.LogPath));
        using var stream = File.Open(log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        Assert.Contains("[stdout] hello", content);
        Assert.Contains("[stderr] warning", content);
    }

    [Fact]
    public async Task SaveArtifactsAsync_CopiesFilesUnderActioArtifacts()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        var reportPath = Path.Combine(projectRoot, "coverage.txt");
        await File.WriteAllTextAsync(reportPath, "coverage");

        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        var runId = "run-artifacts";
        await store.InitializeRunAsync(runId);

        var result = await store.SaveArtifactsAsync(
            runId,
            "test",
            projectRoot,
            [new WorkflowArtifact("coverage", "coverage.txt")]);

        Assert.Empty(result.Errors);
        var artifact = Assert.Single(result.Artifacts);
        Assert.True(File.Exists(artifact.StoredPath));
        Assert.Contains(Path.Combine("artifacts", runId, "test", "coverage"), artifact.StoredPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
