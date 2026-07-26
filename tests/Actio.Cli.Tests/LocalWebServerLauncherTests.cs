using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Actio.Engine.Runs;
using Actio.Storage;

namespace Actio.Cli.Tests;

[Collection("Process environment")]
public sealed class LocalWebServerLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"actio-web-launcher-tests-{Guid.NewGuid():N}");

    [Fact]
    public void CreateStartInfoUsesSnapshotAndSanitizesEnvironment()
    {
        var originalSecret = Environment.GetEnvironmentVariable("ACTIO_SECRET_PHASE67");
        Environment.SetEnvironmentVariable("ACTIO_SECRET_PHASE67", "do-not-copy");
        try
        {
            var snapshot = new WebRuntimeSnapshot(
                "runtime",
                Path.Combine(_root, "runtime"),
                Path.Combine(_root, "runtime", "actio.dll"),
                Path.Combine(_root, "runtime", "actio.exe"),
                UsesDotnetHost: false,
                "1.0.0");

            var startInfo = LocalWebServerLauncher.CreateStartInfo(
                snapshot,
                _root,
                Path.Combine(_root, "home"),
                "http://127.0.0.1:17345",
                "instance",
                "token",
                "session");

            Assert.Equal(snapshot.HostPath, startInfo.FileName);
            Assert.Equal(_root, startInfo.WorkingDirectory);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(startInfo.CreateNewProcessGroup);
            }

            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.DoesNotContain(snapshot.EntryAssemblyPath, startInfo.ArgumentList);
            Assert.Equal("runtime", startInfo.Environment[LocalWebServerLauncher.RuntimeIdentityEnvironmentVariable]);
            Assert.Equal("instance", startInfo.Environment[LocalWebServerLauncher.InstanceIdEnvironmentVariable]);
            Assert.Equal("token", startInfo.Environment[LocalWebServerLauncher.ControlTokenEnvironmentVariable]);
            Assert.Equal("session", startInfo.Environment[LocalWebServerLauncher.SessionIdEnvironmentVariable]);
            Assert.False(startInfo.Environment.ContainsKey("ACTIO_SECRET_PHASE67"));
            Assert.False(startInfo.Environment.ContainsKey("ACTIO_GITHUB_TOKEN"));
            Assert.False(startInfo.Environment.ContainsKey("GITHUB_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTIO_SECRET_PHASE67", originalSecret);
        }
    }

    [Fact]
    public void CreateStartInfoAddsEntrypointForFrameworkDependentRuntime()
    {
        var snapshot = new WebRuntimeSnapshot(
            "runtime",
            Path.Combine(_root, "runtime"),
            Path.Combine(_root, "runtime", "actio.dll"),
            "dotnet",
            UsesDotnetHost: true,
            "1.0.0");

        var startInfo = LocalWebServerLauncher.CreateStartInfo(
            snapshot,
            _root,
            Path.Combine(_root, "home"),
            "http://127.0.0.1:17345",
            "instance",
            "token");

        Assert.Equal(snapshot.EntryAssemblyPath, startInfo.ArgumentList[0]);
    }

    [Fact]
    public void WindowsCommandLineQuotesPathsAndEmbeddedQuotes()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Program Files\Actio\actio.exe"
        };
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add(@"C:\workspace with spaces\");
        startInfo.ArgumentList.Add("value\"quoted");

        var commandLine = DetachedProcessStarter.BuildWindowsCommandLine(startInfo);

        Assert.Equal(
            "\"C:\\Program Files\\Actio\\actio.exe\" web \"C:\\workspace with spaces\\\\\" \"value\\\"quoted\"",
            commandLine);
    }

    [Fact]
    public void BackgroundWorkerContextRejectsBuildOutputFallback()
    {
        var actioHome = Path.Combine(_root, "fallback-home");
        var runtimeIdentity = "runtime";
        var snapshotPath = Path.Combine(actioHome, "web", "runtimes", runtimeIdentity);
        var variables = new Dictionary<string, string?>
        {
            [LocalWebServerLauncher.RuntimeIdentityEnvironmentVariable] = runtimeIdentity,
            [LocalWebServerLauncher.InstanceIdEnvironmentVariable] = "instance",
            [LocalWebServerLauncher.ControlTokenEnvironmentVariable] = "token",
            [LocalWebServerLauncher.SnapshotPathEnvironmentVariable] = snapshotPath
        };
        var originalValues = variables.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);
        try
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }

            var error = new StringWriter();
            var context = CliApplication.ReadWebWorkerContext(
                _root,
                actioHome,
                runtimeIdentity,
                error);

            Assert.Null(context);
            Assert.Contains("does not match its runtime snapshot", error.ToString());
        }
        finally
        {
            foreach (var variable in originalValues)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
        }
    }

    [Fact]
    public async Task ConcurrentProjectsUseIndependentDynamicLoopbackPorts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = GetReleaseCliOutput();
        var actioHome = Path.Combine(_root, "concurrent-home");
        var firstProject = Path.Combine(_root, "first-project");
        var secondProject = Path.Combine(_root, "second-project");
        Directory.CreateDirectory(firstProject);
        Directory.CreateDirectory(secondProject);
        var manager = new WebRuntimeSnapshotManager(
            sourceRoot,
            Path.Combine(sourceRoot, "actio.exe"),
            Path.Combine(sourceRoot, "actio.dll"),
            CliVersion.GetVersion());
        var launcher = new LocalWebServerLauncher(
            Actio.Web.ActioWebDefaults.DefaultUrl,
            actioHome,
            TimeSpan.FromSeconds(15),
            manager,
            static () => new HttpClient(),
            useProjectSessions: true);
        var firstDiagnostics = new StringWriter();
        var secondDiagnostics = new StringWriter();
        await SaveRunAsync(actioHome, firstProject, "first-run");
        await SaveRunAsync(actioHome, secondProject, "second-run");

        var viewUrls = await Task.WhenAll(
            launcher.EnsureStartedAsync(firstProject, "first-run", firstDiagnostics),
            launcher.EnsureStartedAsync(secondProject, "second-run", secondDiagnostics));

        Assert.True(viewUrls[0] is not null, firstDiagnostics.ToString());
        Assert.True(viewUrls[1] is not null, secondDiagnostics.ToString());
        var firstBaseUrl = GetBaseUrl(viewUrls[0]!);
        var secondBaseUrl = GetBaseUrl(viewUrls[1]!);
        Assert.NotEqual(firstBaseUrl, secondBaseUrl);
        Assert.NotEqual(0, new Uri(firstBaseUrl).Port);
        Assert.NotEqual(0, new Uri(secondBaseUrl).Port);

        var repeatedUrl = await launcher.EnsureStartedAsync(
            firstProject,
            "repeated-run",
            firstDiagnostics);
        Assert.Equal($"{firstBaseUrl}/runs/repeated-run", repeatedUrl);

        using var http = new HttpClient();
        var firstHealth = await http.GetFromJsonAsync<WebHealthProbe>($"{firstBaseUrl}/api/health");
        var secondHealth = await http.GetFromJsonAsync<WebHealthProbe>($"{secondBaseUrl}/api/health");
        Assert.True(WebProjectSessionPathsEqual(firstProject, firstHealth?.ProjectRoot));
        Assert.True(WebProjectSessionPathsEqual(secondProject, secondHealth?.ProjectRoot));
        Assert.NotEqual(firstHealth?.SessionId, secondHealth?.SessionId);
        using var firstRuns = await http.GetFromJsonAsync<JsonDocument>(
            $"{firstBaseUrl}/api/runs");
        using var secondRuns = await http.GetFromJsonAsync<JsonDocument>(
            $"{secondBaseUrl}/api/runs");
        Assert.Equal(
            ["first-run"],
            firstRuns!.RootElement.EnumerateArray()
                .Select(run => run.GetProperty("runId").GetString()!)
                .ToArray());
        Assert.Equal(
            ["second-run"],
            secondRuns!.RootElement.EnumerateArray()
                .Select(run => run.GetProperty("runId").GetString()!)
                .ToArray());

        await ShutdownAsync(actioHome, firstProject, firstBaseUrl);
        await ShutdownAsync(actioHome, secondProject, secondBaseUrl);
    }

    [Fact]
    public async Task DuplicateActiveProjectRecordsFailWithoutSelectingAProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = GetReleaseCliOutput();
        var projectRoot = Path.Combine(_root, "duplicate-project");
        var actioHome = Path.Combine(_root, "duplicate-home");
        Directory.CreateDirectory(projectRoot);
        var session = WebProjectSession.Create(projectRoot, actioHome);
        using var currentProcess = Process.GetCurrentProcess();
        var sharedMetadata = new WebProcessMetadata(
            2,
            currentProcess.Id,
            currentProcess.StartTime.ToUniversalTime().Ticks,
            "session-instance",
            WebProcessMetadataStore.CreateOwnershipToken(),
            "runtime",
            Path.Combine(actioHome, "snapshot"),
            Environment.ProcessPath!,
            "1.0.0",
            "http://127.0.0.1:17346",
            session.ProjectRoot,
            session.ActioHome,
            DateTimeOffset.UtcNow,
            session.Id);
        WebProcessMetadataStore.ForProject(actioHome, session.Id).Save(sharedMetadata);
        new WebProcessMetadataStore(actioHome, "http://127.0.0.1:17347").Save(
            sharedMetadata with
            {
                SchemaVersion = 1,
                InstanceId = "legacy-instance",
                Url = "http://127.0.0.1:17347",
                SessionId = null
            });
        var launcher = new LocalWebServerLauncher(
            Actio.Web.ActioWebDefaults.DefaultUrl,
            actioHome,
            TimeSpan.FromSeconds(5),
            new WebRuntimeSnapshotManager(
                sourceRoot,
                Path.Combine(sourceRoot, "actio.exe"),
                Path.Combine(sourceRoot, "actio.dll"),
                CliVersion.GetVersion()),
            static () => new HttpClient(),
            useProjectSessions: true);
        var diagnostics = new StringWriter();

        var result = await launcher.EnsureStartedAsync(
            projectRoot,
            "run",
            diagnostics);

        Assert.Null(result);
        Assert.Contains(
            "multiple active process records",
            diagnostics.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OccupiedForegroundUrlForAnotherProjectDoesNotBlockDynamicWorker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = GetReleaseCliOutput();
        var actioHome = Path.Combine(_root, "occupied-home");
        var foregroundProject = Path.Combine(_root, "foreground-project");
        var workerProject = Path.Combine(_root, "worker-project");
        Directory.CreateDirectory(actioHome);
        Directory.CreateDirectory(foregroundProject);
        Directory.CreateDirectory(workerProject);
        var occupiedUrl = $"http://127.0.0.1:{GetAvailablePort()}";
        var manager = new WebRuntimeSnapshotManager(
            sourceRoot,
            Path.Combine(sourceRoot, "actio.exe"),
            Path.Combine(sourceRoot, "actio.dll"),
            CliVersion.GetVersion());
        var runtime = manager.DescribeCurrent();
        using var foregroundCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var foregroundStarted = new TaskCompletionSource<Actio.Web.ActioWebServerBinding>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundTask = new Actio.Web.ActioWebServer().RunAsync(
            new Actio.Web.ActioWebOptions(
                foregroundProject,
                actioHome,
                occupiedUrl,
                RuntimeIdentity: runtime.Identity),
            (binding, _) =>
            {
                foregroundStarted.TrySetResult(binding);
                return Task.CompletedTask;
            },
            foregroundCancellation.Token);
        await foregroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var launcher = new LocalWebServerLauncher(
            occupiedUrl,
            actioHome,
            TimeSpan.FromSeconds(15),
            manager,
            static () => new HttpClient(),
            useProjectSessions: true);
        var diagnostics = new StringWriter();

        var viewUrl = await launcher.EnsureStartedAsync(
            workerProject,
            "run",
            diagnostics);

        Assert.True(viewUrl is not null, diagnostics.ToString());
        var workerUrl = GetBaseUrl(viewUrl!);
        Assert.NotEqual(occupiedUrl, workerUrl);
        await ShutdownAsync(actioHome, workerProject, workerUrl);

        foregroundCancellation.Cancel();
        try
        {
            await foregroundTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task BackgroundWorkerRunsFromSnapshotWithoutLockingSourceAssembly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = GetReleaseCliOutput();
        var appHost = Path.Combine(sourceRoot, "actio.exe");
        var entryAssembly = Path.Combine(sourceRoot, "actio.dll");
        Assert.True(File.Exists(appHost));
        Assert.True(File.Exists(entryAssembly));

        var projectRoot = Path.Combine(_root, "project");
        var actioHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".workflows"));
        var url = $"http://127.0.0.1:{GetAvailablePort()}";
        var manager = new WebRuntimeSnapshotManager(
            sourceRoot,
            appHost,
            entryAssembly,
            CliVersion.GetVersion());
        var launcher = new LocalWebServerLauncher(
            url,
            actioHome,
            TimeSpan.FromSeconds(15),
            manager,
            static () => new HttpClient());
        var diagnostics = new StringWriter();

        var viewUrl = await launcher.EnsureStartedAsync(
            projectRoot,
            "run-1",
            diagnostics);

        Assert.True(viewUrl is not null, diagnostics.ToString());
        Assert.Equal($"{url}/runs/run-1", viewUrl);
        using (File.Open(entryAssembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        var repositoryRoot = FindRepositoryRoot();
        await AssertCommandSucceedsAsync(
            repositoryRoot,
            "build",
            "src/Actio.Cli/Actio.Cli.csproj",
            "--configuration",
            "Release",
            "--no-restore");
        await AssertCommandSucceedsAsync(
            repositoryRoot,
            "run",
            "--project",
            "src/Actio.Cli/Actio.Cli.csproj",
            "--configuration",
            "Release",
            "--no-build",
            "--",
            "--version");

        var store = new WebProcessMetadataStore(actioHome, url);
        var metadata = Assert.IsType<WebProcessMetadata>(store.Read().Metadata);
        Assert.StartsWith(
            Path.Combine(actioHome, "web", "runtimes"),
            metadata.SnapshotPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(sourceRoot.TrimEnd(Path.DirectorySeparatorChar), metadata.SnapshotPath);

        using var http = new HttpClient();
        using var shutdown = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/internal/shutdown");
        shutdown.Headers.Add("X-Actio-Control-Token", metadata.OwnershipToken);
        using var response = await http.SendAsync(shutdown);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await WaitForExitAsync(metadata);
        Assert.Equal(
            WebOwnerState.Stale,
            WebProcessMetadataStore.GetOwnerState(
                metadata.ProcessId,
                metadata.ProcessStartTimeUtcTicks));
    }

    [Fact]
    public async Task CancellationStopsWorkerStartedByCurrentLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = GetReleaseCliOutput();
        var projectRoot = Path.Combine(_root, "cancel-project");
        var actioHome = Path.Combine(_root, "cancel-home");
        Directory.CreateDirectory(projectRoot);
        var manager = new WebRuntimeSnapshotManager(
            sourceRoot,
            Path.Combine(sourceRoot, "actio.exe"),
            Path.Combine(sourceRoot, "actio.dll"),
            CliVersion.GetVersion());
        var launcher = new LocalWebServerLauncher(
            $"http://127.0.0.1:{GetAvailablePort()}",
            actioHome,
            TimeSpan.FromSeconds(15),
            manager,
            static () => new HttpClient(new OfflineHttpHandler()));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launcher.EnsureStartedAsync(
                projectRoot,
                "run-cancelled",
                TextWriter.Null,
                cancellation.Token));

        await WaitForNoOwnedProcessesAsync(_root);
    }

    public void Dispose()
    {
        foreach (var process in Process.GetProcessesByName("actio"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName.StartsWith(
                        _root,
                        StringComparison.OrdinalIgnoreCase) == true)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                }
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetBaseUrl(string runUrl)
    {
        var uri = new Uri(runUrl);
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    private static bool WebProjectSessionPathsEqual(string expected, string? actual)
    {
        return actual is not null &&
            string.Equals(
                Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private static async Task ShutdownAsync(
        string actioHome,
        string projectRoot,
        string serverUrl)
    {
        var session = WebProjectSession.Create(projectRoot, actioHome);
        var store = WebProcessMetadataStore.ForProject(actioHome, session.Id);
        var metadata = Assert.IsType<WebProcessMetadata>(store.Read().Metadata);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{serverUrl}/api/internal/shutdown");
        request.Headers.Add("X-Actio-Control-Token", metadata.OwnershipToken);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForExitAsync(metadata);
    }

    private static async Task SaveRunAsync(
        string actioHome,
        string projectRoot,
        string runId)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new WorkflowRunRecord(
            runId,
            "CI",
            null,
            projectRoot,
            "Success",
            now,
            now,
            0,
            [],
            [],
            [],
            []);
        var store = new FileSystemRunStore(actioHome);
        await store.InitializeRunAsync(runId);
        await store.SaveRunRecordAsync(record);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Actio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Actio repository root could not be found.");
    }

    private static string GetReleaseCliOutput()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Actio.Cli",
            "bin",
            "Release",
            "net10.0");
    }

    private static async Task WaitForExitAsync(WebProcessMetadata metadata)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (WebProcessMetadataStore.GetOwnerState(
                metadata.ProcessId,
                metadata.ProcessStartTimeUtcTicks) == WebOwnerState.Stale)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static async Task AssertCommandSucceedsAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("dotnet verification process could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static async Task WaitForNoOwnedProcessesAsync(string root)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = false;
            foreach (var process in Process.GetProcessesByName("actio"))
            {
                using (process)
                {
                    try
                    {
                        found |= process.MainModule?.FileName.StartsWith(
                            root,
                            StringComparison.OrdinalIgnoreCase) == true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                    {
                    }
                }
            }

            if (!found)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("A cancelled launch left its managed web worker running.");
    }

    private sealed class OfflineHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed record WebHealthProbe(
        string? ProjectRoot,
        string? SessionId);
}

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
