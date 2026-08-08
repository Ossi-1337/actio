using Actio.Core.Workflows;
using Actio.Engine.Execution;
using Actio.Engine.Runs;
using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class FileSystemRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-storage-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializeRunAsync_CreatesRunSpecificWorkspaceMaskFiles()
    {
        var store = new FileSystemRunStore(_root);

        var paths = await store.InitializeRunAsync("run-isolation");

        Assert.Equal(Path.GetFullPath(_root), paths.ActioHomePath);
        Assert.Equal(2, paths.WorkspaceMaskFiles.Count);
        Assert.All(paths.WorkspaceMaskFiles.Values, path =>
        {
            Assert.True(File.Exists(path));
            Assert.Equal(string.Empty, File.ReadAllText(path));
            Assert.StartsWith(paths.RunDirectory!, path, StringComparison.OrdinalIgnoreCase);
        });
    }

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
            [],
            [
                new WorkflowTrigger(
                    "push",
                    null,
                    new WorkflowTriggerFilters(["main"], [], [], [], ["src/**"], []))
            ],
            new WorkflowRunTrigger(
                "workflow_dispatch",
                "CLI",
                new Dictionary<string, string> { ["environment"] = "staging" }),
            RunnerSecurity: new RunnerSecurityMetadata(
                "docker",
                "secure-baseline",
                "secure-baseline",
                ["no-new-privileges=true"],
                NetworkPolicy: "per-job-user-defined-bridge-with-outbound",
                PublishedPortPolicy: "ipv4-loopback-only",
                NetworkObservations:
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
                Preflight: new RunnerPreflightEvidence(Status: "passed", EngineVersion: "29.0.0"),
                Cleanup: new RunnerCleanupEvidence(RemovedContainers: 1),
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
                ]));

        await store.SaveRunRecordAsync(record);
        var loaded = await store.ReadRunRecordAsync(runId);

        Assert.NotNull(loaded);
        Assert.Equal("CI", loaded.WorkflowName);
        var trigger = Assert.Single(loaded.Triggers);
        Assert.Equal("push", trigger.EventName);
        Assert.Equal(["main"], trigger.Filters.Branches);
        Assert.Equal(["src/**"], trigger.Filters.Paths);
        Assert.Equal("workflow_dispatch", loaded.RunTrigger.EventName);
        Assert.Equal("CLI", loaded.RunTrigger.Source);
        Assert.Equal("staging", loaded.RunTrigger.Inputs["environment"]);
        Assert.Equal("workflow_dispatch", loaded.RunTrigger.EventPayload.EventName);
        Assert.Equal("CLI", loaded.RunTrigger.EventPayload.Source);
        Assert.Equal("staging", loaded.RunTrigger.EventPayload.Inputs["environment"]);
        Assert.NotNull(loaded.RunnerSecurity);
        Assert.Equal("docker", loaded.RunnerSecurity.Provider);
        Assert.Contains("no-new-privileges=true", loaded.RunnerSecurity.AppliedSecurityOptions);
        Assert.Equal("ipv4-loopback-only", loaded.RunnerSecurity.PublishedPortPolicy);
        var network = Assert.Single(loaded.RunnerSecurity.NetworkObservations);
        Assert.Equal("actio-test-network", network.NetworkName);
        Assert.Equal(15432, Assert.Single(network.PublishedPorts).HostPort);
        Assert.Equal(2, loaded.RunnerSecurity.EffectiveResourceLimits?.Cpu);
        Assert.Equal("passed", loaded.RunnerSecurity.Preflight?.Status);
        Assert.Equal(1, loaded.RunnerSecurity.Cleanup?.RemovedContainers);
        Assert.Contains("cap-drop-all", loaded.RunnerSecurity.StrictControls);
        Assert.Equal("node24", Assert.Single(loaded.RunnerSecurity.JavaScriptRuntimeObservations).Runtime);
        Assert.Equal("24.18.0", Assert.Single(loaded.RunnerSecurity.JavaScriptRuntimeObservations).NodeVersion);
        Assert.True(File.Exists(Path.Combine(_root, "runs", runId, "run.json")));
    }

    [Fact]
    public async Task ListRunRecordsAsync_ReturnsSavedRunRecords()
    {
        var store = new FileSystemRunStore(_root);
        await store.InitializeRunAsync("run-1");
        await store.SaveRunRecordAsync(new WorkflowRunRecord(
            "run-1",
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
            []));

        var records = await store.ListRunRecordsAsync();

        var record = Assert.Single(records);
        Assert.Equal("run-1", record.RunId);
    }

    [Fact]
    public async Task RunRecordReadersAllowConcurrentAtomicProgressWrites()
    {
        var store = new FileSystemRunStore(_root);
        const string runId = "run-stress";
        await store.InitializeRunAsync(runId);

        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 100; index++)
            {
                await store.SaveRunRecordAsync(new WorkflowRunRecord(
                    runId,
                    "CI",
                    null,
                    _root,
                    "Running",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    index,
                    [],
                    [],
                    [],
                    []));
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                await store.ReadRunRecordAsync(runId);
                await store.ListRunRecordsAsync();
            }
        }));

        await Task.WhenAll(readers.Append(writer));

        Assert.NotNull(await store.ReadRunRecordAsync(runId));
    }

    [Fact]
    public async Task ReadRunRecordAsync_ReturnsEmptyTriggersForOlderRunRecords()
    {
        var store = new FileSystemRunStore(_root);
        var runId = "run-old";
        await store.InitializeRunAsync(runId);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "runs", runId, "run.json"),
            """
            {
              "RunId": "run-old",
              "WorkflowName": "CI",
              "WorkflowPath": "C:\\repo\\.workflows\\ci.yml",
              "ProjectRoot": "C:\\repo",
              "Status": "Success",
              "StartedAt": "2026-06-25T10:00:00+00:00",
              "FinishedAt": "2026-06-25T10:00:01+00:00",
              "DurationMilliseconds": 1000,
              "Jobs": [],
              "Outputs": [],
              "Artifacts": [],
              "Errors": []
            }
            """);

        var loaded = await store.ReadRunRecordAsync(runId);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Triggers);
        Assert.Equal("workflow_dispatch", loaded.RunTrigger.EventName);
        Assert.Equal("CLI", loaded.RunTrigger.Source);
        Assert.Empty(loaded.SecurityFindings);
        Assert.Null(loaded.RunnerSecurity);
        Assert.Equal("workflow_dispatch", loaded.RunTrigger.EventPayload.EventName);
        Assert.Equal("CLI", loaded.RunTrigger.EventPayload.Source);
    }

    [Fact]
    public async Task ReadRunRecordAsync_DefaultsNetworkMetadataForOlderSecurityRecords()
    {
        var store = new FileSystemRunStore(_root);
        const string runId = "run-old-security";
        await store.InitializeRunAsync(runId);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "runs", runId, "run.json"),
            """
            {
              "RunId": "run-old-security",
              "WorkflowName": "CI",
              "WorkflowPath": "C:\\repo\\.workflows\\ci.yml",
              "ProjectRoot": "C:\\repo",
              "Status": "Success",
              "StartedAt": "2026-06-25T10:00:00+00:00",
              "FinishedAt": "2026-06-25T10:00:01+00:00",
              "DurationMilliseconds": 1000,
              "Jobs": [],
              "Outputs": [],
              "Artifacts": [],
              "Errors": [],
              "RunnerSecurity": {
                "Provider": "docker",
                "RequestedProfile": "secure-baseline",
                "EffectiveProfile": "secure-baseline"
              }
            }
            """);

        var loaded = await store.ReadRunRecordAsync(runId);

        Assert.NotNull(loaded?.RunnerSecurity);
        Assert.Equal("not-reported", loaded.RunnerSecurity.NetworkPolicy);
        Assert.Equal("not-reported", loaded.RunnerSecurity.PublishedPortPolicy);
        Assert.Empty(loaded.RunnerSecurity.NetworkObservations);
        Assert.Empty(loaded.RunnerSecurity.JavaScriptRuntimeObservations);
    }

    [Fact]
    public async Task ReadRunRecordAsync_ReturnsEmptyTriggerFiltersForOlderTriggerRecords()
    {
        var store = new FileSystemRunStore(_root);
        var runId = "run-old-trigger";
        await store.InitializeRunAsync(runId);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "runs", runId, "run.json"),
            """
            {
              "RunId": "run-old-trigger",
              "WorkflowName": "CI",
              "WorkflowPath": "C:\\repo\\.workflows\\ci.yml",
              "ProjectRoot": "C:\\repo",
              "Status": "Success",
              "StartedAt": "2026-06-25T10:00:00+00:00",
              "FinishedAt": "2026-06-25T10:00:01+00:00",
              "DurationMilliseconds": 1000,
              "Jobs": [],
              "Outputs": [],
              "Artifacts": [],
              "Errors": [],
              "Triggers": [
                {
                  "EventName": "push",
                  "Configuration": null
                }
              ]
            }
            """);

        var loaded = await store.ReadRunRecordAsync(runId);

        Assert.NotNull(loaded);
        var trigger = Assert.Single(loaded.Triggers);
        Assert.Equal("push", trigger.EventName);
        Assert.Empty(trigger.Filters.Branches);
        Assert.Empty(trigger.Filters.Paths);
        Assert.Empty(trigger.ActivityTypes);
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
    public async Task RequestRunCancellationAsync_WritesCancellationMarker()
    {
        var store = new FileSystemRunStore(_root);
        var runId = "run-cancel";
        await store.InitializeRunAsync(runId);

        Assert.False(await store.IsRunCancellationRequestedAsync(runId));

        await store.RequestRunCancellationAsync(runId);

        Assert.True(await store.IsRunCancellationRequestedAsync(runId));
        Assert.True(File.Exists(Path.Combine(_root, "runs", runId, "cancel.requested")));
    }

    [Fact]
    public async Task CreateStepEnvironmentFilesAsync_CreatesFilesUnderRunDirectory()
    {
        var store = new FileSystemRunStore(_root);
        var runId = "run-env";
        await store.InitializeRunAsync(runId);

        var files = await store.CreateStepEnvironmentFilesAsync(
            runId,
            "test",
            0,
            "Run tests");

        Assert.Contains(Path.Combine("runs", runId, "env-files", "test"), files.DirectoryPath);
        Assert.True(File.Exists(files.EnvironmentFilePath));
        Assert.True(File.Exists(files.OutputFilePath));
        Assert.True(File.Exists(files.PathFilePath));
        Assert.True(File.Exists(files.StepSummaryFilePath));
        Assert.True(File.Exists(files.StateFilePath));
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
        AssertArtifactAttestation(artifact, expectedFileCount: 1, expectedTotalBytes: 8);
    }

    [Fact]
    public async Task SaveArtifactsAsync_ValidatesEverySourceBeforeWritingAnyArtifact()
    {
        var projectRoot = Path.Combine(_root, "repo-atomic-artifacts");
        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "coverage.txt"), "coverage");
        var store = new FileSystemRunStore(Path.Combine(_root, "home-atomic-artifacts"));

        var result = await store.SaveArtifactsAsync(
            "run-atomic",
            "test",
            projectRoot,
            [
                new WorkflowArtifact("coverage", "coverage.txt"),
                new WorkflowArtifact("missing", "missing.txt")
            ]);

        Assert.Empty(result.Artifacts);
        Assert.NotEmpty(result.Errors);
        Assert.False(Directory.Exists(Path.Combine(store.ArtifactsPath, "run-atomic")));
    }

    [Fact]
    public async Task SaveArtifactAsync_CopiesMultiplePathsAndStoresRetentionMetadata()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "coverage.txt"), "coverage");
        Directory.CreateDirectory(Path.Combine(projectRoot, "logs"));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "logs", "test.log"), "log");

        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        var runId = "run-upload-artifact";
        await store.InitializeRunAsync(runId);

        var result = await store.SaveArtifactAsync(
            runId,
            "test",
            projectRoot,
            "bundle",
            ["coverage.txt", "logs"],
            retentionDays: 7);

        Assert.Empty(result.Errors);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(7, artifact.RetentionDays);
        Assert.True(Directory.Exists(artifact.StoredPath));
        Assert.True(File.Exists(Path.Combine(artifact.StoredPath, "coverage.txt")));
        Assert.True(File.Exists(Path.Combine(artifact.StoredPath, "test.log")));
        AssertArtifactAttestation(artifact, expectedFileCount: 2, expectedTotalBytes: 11);
    }

    [Fact]
    public async Task SaveArtifactAsync_CreatesDeterministicContentDigest()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        var reportPath = Path.Combine(projectRoot, "report.txt");
        await File.WriteAllTextAsync(reportPath, "first");

        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await store.InitializeRunAsync("run-digest-1");
        await store.InitializeRunAsync("run-digest-2");
        await store.InitializeRunAsync("run-digest-3");

        var first = await store.SaveArtifactAsync(
            "run-digest-1",
            "test",
            projectRoot,
            "report",
            ["report.txt"]);
        var second = await store.SaveArtifactAsync(
            "run-digest-2",
            "test",
            projectRoot,
            "report",
            ["report.txt"]);

        await File.WriteAllTextAsync(reportPath, "second");
        var changed = await store.SaveArtifactAsync(
            "run-digest-3",
            "test",
            projectRoot,
            "report",
            ["report.txt"]);

        var firstDigest = Assert.Single(first.Artifacts).Attestation?.Digest;
        var secondDigest = Assert.Single(second.Artifacts).Attestation?.Digest;
        var changedDigest = Assert.Single(changed.Artifacts).Attestation?.Digest;
        Assert.NotNull(firstDigest);
        Assert.NotNull(secondDigest);
        Assert.NotNull(changedDigest);
        Assert.Equal(firstDigest, secondDigest);
        Assert.NotEqual(firstDigest, changedDigest);
    }

    [Fact]
    public async Task SaveArtifactAsync_RejectsPathsOutsideProjectRoot()
    {
        var projectRoot = Path.Combine(_root, "repo");
        var outsideRoot = Path.Combine(_root, "outside");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outsideRoot);
        await File.WriteAllTextAsync(Path.Combine(outsideRoot, "secret.txt"), "secret");

        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await store.InitializeRunAsync("run-outside-upload");

        var result = await store.SaveArtifactAsync(
            "run-outside-upload",
            "test",
            projectRoot,
            "secret",
            [Path.Combine("..", "outside", "secret.txt")]);

        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Errors, error => error.Contains("must stay inside the project root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveArtifactAsync_RejectsStoragePathTraversalFromIdentifiers()
    {
        var projectRoot = Path.Combine(_root, "repo-artifact-target");
        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "report.txt"), "report");
        var actioHome = Path.Combine(_root, "home-artifact-target");
        var store = new FileSystemRunStore(actioHome);

        var result = await store.SaveArtifactAsync(
            "run-artifact-target",
            "..",
            projectRoot,
            "..",
            ["report.txt"]);

        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Errors, error =>
            error.Contains("storage path must stay inside the run artifact directory", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(actioHome, "report.txt")));
    }

    [Fact]
    public async Task SaveArtifactAsync_SeparatesNamesThatSanitizeToSamePath()
    {
        var projectRoot = Path.Combine(_root, "repo-artifact-collision");
        Directory.CreateDirectory(Path.Combine(projectRoot, "first"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "second"));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "first", "report.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "second", "report.txt"), "second");
        var store = new FileSystemRunStore(Path.Combine(_root, "home-artifact-collision"));

        var first = await store.SaveArtifactAsync(
            "run-artifact-collision",
            "test",
            projectRoot,
            "release notes",
            [Path.Combine("first", "report.txt")]);
        var second = await store.SaveArtifactAsync(
            "run-artifact-collision",
            "test",
            projectRoot,
            "release-notes",
            [Path.Combine("second", "report.txt")]);

        var firstArtifact = Assert.Single(first.Artifacts);
        var secondArtifact = Assert.Single(second.Artifacts);
        Assert.NotEqual(firstArtifact.StoredPath, secondArtifact.StoredPath);
        Assert.Equal("first", await File.ReadAllTextAsync(firstArtifact.StoredPath));
        Assert.Equal("second", await File.ReadAllTextAsync(secondArtifact.StoredPath));
        Assert.NotEqual(firstArtifact.Attestation?.Digest, secondArtifact.Attestation?.Digest);

        var restore = await store.RestoreArtifactsAsync(
            projectRoot,
            [firstArtifact, secondArtifact],
            "restored",
            useArtifactNameSubdirectories: true);

        Assert.Empty(restore.Errors);
        Assert.Equal(2, restore.RestoredPaths.Count);
        Assert.Equal(
            2,
            restore.RestoredPaths
                .Select(path => Path.GetDirectoryName(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        var restoredContents = await Task.WhenAll(
            restore.RestoredPaths.Select(path => File.ReadAllTextAsync(path)));
        Assert.Equal(["first", "second"], restoredContents.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaveArtifactAsync_RejectsLinksBeforeCreatingArtifactContent()
    {
        var projectRoot = Path.Combine(_root, "repo-link");
        var source = Path.Combine(projectRoot, "output");
        var outside = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(outside, "host-secret");

        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(source, "linked.txt"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var store = new FileSystemRunStore(Path.Combine(_root, "home"));
            var result = await store.SaveArtifactAsync(
                "run-link",
                "test",
                projectRoot,
                "report",
                ["output"]);

            Assert.NotEmpty(result.Errors);
            Assert.Contains(result.Errors, error => error.Contains("linked.txt", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(store.ArtifactsPath, "run-link")));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task RestoreArtifactsAsync_RestoresNamedFileArtifactToDestination()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        var reportPath = Path.Combine(projectRoot, "coverage.txt");
        await File.WriteAllTextAsync(reportPath, "coverage");

        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        var runId = "run-restore-artifact";
        await store.InitializeRunAsync(runId);
        var save = await store.SaveArtifactsAsync(
            runId,
            "test",
            projectRoot,
            [new WorkflowArtifact("coverage", "coverage.txt")]);
        File.Delete(reportPath);

        var restore = await store.RestoreArtifactsAsync(
            projectRoot,
            save.Artifacts,
            "downloaded",
            useArtifactNameSubdirectories: false);

        Assert.Empty(restore.Errors);
        Assert.True(File.Exists(Path.Combine(projectRoot, "downloaded", "coverage.txt")));
    }

    [Fact]
    public async Task RestoreArtifactsAsync_RestoresAllArtifactsWithSubdirectories()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "coverage.txt"), "coverage");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "summary.txt"), "summary");

        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        var runId = "run-restore-all-artifacts";
        await store.InitializeRunAsync(runId);
        var first = await store.SaveArtifactsAsync(
            runId,
            "test",
            projectRoot,
            [new WorkflowArtifact("coverage", "coverage.txt")]);
        var second = await store.SaveArtifactsAsync(
            runId,
            "test",
            projectRoot,
            [new WorkflowArtifact("summary", "summary.txt")]);

        var restore = await store.RestoreArtifactsAsync(
            projectRoot,
            first.Artifacts.Concat(second.Artifacts).ToArray(),
            "downloaded",
            useArtifactNameSubdirectories: true);

        Assert.Empty(restore.Errors);
        Assert.True(File.Exists(Path.Combine(projectRoot, "downloaded", "coverage", "coverage.txt")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "downloaded", "summary", "summary.txt")));
    }

    [Fact]
    public async Task RestoreArtifactsAsync_RejectsDestinationOutsideProjectRoot()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await store.InitializeRunAsync("run-outside-restore");

        var result = await store.RestoreArtifactsAsync(
            projectRoot,
            [],
            Path.Combine("..", "outside"),
            useArtifactNameSubdirectories: false);

        Assert.Empty(result.RestoredPaths);
        Assert.Contains(result.Errors, error => error.Contains("must stay inside the project root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreArtifactsAsync_FailsWhenStoredPathIsMissing()
    {
        var projectRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(projectRoot);
        var store = new FileSystemRunStore(Path.Combine(_root, "actio-home"));
        await store.InitializeRunAsync("run-missing-stored-artifact");
        var missingPath = Path.Combine(store.ArtifactsPath, "run-missing-stored-artifact", "test", "report", "report.txt");

        var result = await store.RestoreArtifactsAsync(
            projectRoot,
            [new WorkflowRunArtifact("test", "report", "report.txt", missingPath)],
            "downloaded",
            useArtifactNameSubdirectories: false);

        Assert.Empty(result.RestoredPaths);
        Assert.Contains(result.Errors, error => error.Contains("does not exist", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertArtifactAttestation(
        WorkflowRunArtifact artifact,
        int expectedFileCount,
        long expectedTotalBytes)
    {
        Assert.NotNull(artifact.Attestation);
        Assert.Equal("actio.local-artifact-attestation.v1", artifact.Attestation.Format);
        Assert.Equal("local-unsigned", artifact.Attestation.TrustModel);
        Assert.Equal("sha256", artifact.Attestation.DigestAlgorithm);
        Assert.Equal(64, artifact.Attestation.Digest.Length);
        Assert.Equal(expectedFileCount, artifact.Attestation.FileCount);
        Assert.Equal(expectedTotalBytes, artifact.Attestation.TotalBytes);
    }
}
