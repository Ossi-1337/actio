using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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
                "token");

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
}

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
