using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Actio.Storage;
using Actio.Web;

namespace Actio.Cli;

public sealed class LocalWebServerLauncher : ILocalWebServerLauncher
{
    internal const string RuntimeIdentityEnvironmentVariable = "ACTIO_WEB_RUNTIME_IDENTITY";
    internal const string InstanceIdEnvironmentVariable = "ACTIO_WEB_INSTANCE_ID";
    internal const string ControlTokenEnvironmentVariable = "ACTIO_WEB_CONTROL_TOKEN";
    internal const string SnapshotPathEnvironmentVariable = "ACTIO_WEB_SNAPSHOT_PATH";

    private static readonly string[] PreservedEnvironmentVariables =
    [
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "WINDIR",
        "COMSPEC",
        "TEMP",
        "TMP",
        "HOME",
        "USERPROFILE",
        "DOTNET_ROOT",
        "DOTNET_ROOT(x86)",
        "DOTNET_MULTILEVEL_LOOKUP",
        "LD_LIBRARY_PATH",
        "DYLD_LIBRARY_PATH",
        "LANG",
        "LC_ALL",
        "TZ"
    ];

    private readonly string _url;
    private readonly string _actioHome;
    private readonly TimeSpan _startupTimeout;
    private readonly WebRuntimeSnapshotManager _snapshotManager;
    private readonly Func<HttpClient> _httpClientFactory;

    public LocalWebServerLauncher()
        : this(ActioWebDefaults.DefaultUrl, ActioHome.Resolve(), TimeSpan.FromSeconds(3))
    {
    }

    public LocalWebServerLauncher(string url, string actioHome, TimeSpan startupTimeout)
        : this(
            url,
            actioHome,
            startupTimeout,
            WebRuntimeSnapshotManager.CreateCurrent(),
            static () => new HttpClient())
    {
    }

    internal LocalWebServerLauncher(
        string url,
        string actioHome,
        TimeSpan startupTimeout,
        WebRuntimeSnapshotManager snapshotManager,
        Func<HttpClient> httpClientFactory)
    {
        _url = url.TrimEnd('/');
        _actioHome = Path.GetFullPath(actioHome);
        _startupTimeout = startupTimeout;
        _snapshotManager = snapshotManager;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string?> EnsureStartedAsync(
        string projectRoot,
        string? runId,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var runUrl = runId is null
            ? _url
            : $"{_url}/runs/{Uri.EscapeDataString(runId)}";
        var store = new WebProcessMetadataStore(_actioHome, _url);

        WebRuntimeSnapshot snapshot;
        try
        {
            snapshot = _snapshotManager.Prepare(_actioHome, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableLifecycleError(ex))
        {
            error.WriteLine($"Actio web UI runtime snapshot could not be prepared: {ex.Message}");
            return null;
        }

        FileStream? launchLock;
        try
        {
            launchLock = await WebFileLock.TryAcquireAsync(
                store.LaunchLockPath,
                _startupTimeout,
                cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableLifecycleError(ex))
        {
            error.WriteLine($"Actio web UI launch lock could not be acquired: {ex.Message}");
            return null;
        }

        if (launchLock is null)
        {
            error.WriteLine($"Actio web UI launch lock was not available for '{_url}' before the startup timeout.");
            return null;
        }

        await using var acquiredLaunchLock = launchLock;
        var health = await GetHealthAsync(fullProjectRoot, snapshot.Identity, cancellationToken);
        if (health.Status == WebServerHealth.Ready)
        {
            return runUrl;
        }

        if (health.Status == WebServerHealth.DifferentContext)
        {
            WriteContextMismatch(error);
            return null;
        }

        var skipSnapshotCleanup = false;
        if (health.Status == WebServerHealth.IncompatibleRuntime)
        {
            if (!await StopVerifiedProcessAsync(store, health.Response, error, cancellationToken))
            {
                return null;
            }
        }
        else
        {
            var metadataDecision = HandleOfflineMetadata(store, error);
            if (metadataDecision == OfflineMetadataDecision.Block)
            {
                return null;
            }

            skipSnapshotCleanup =
                metadataDecision == OfflineMetadataDecision.ProceedWithoutSnapshotCleanup;
        }

        if (skipSnapshotCleanup)
        {
            error.WriteLine(
                "Actio web UI snapshot cleanup was skipped because quarantined metadata may reference an active runtime.");
        }
        else if (TryGetProtectedRuntimeIdentities(store, out var protectedRuntimeIdentities))
        {
            CleanupSnapshots(error, snapshot.Identity, protectedRuntimeIdentities);
        }
        else
        {
            error.WriteLine(
                "Actio web UI snapshot cleanup was skipped because process metadata could not be verified.");
        }

        var instanceId = Guid.NewGuid().ToString("N");
        var ownershipToken = WebProcessMetadataStore.CreateOwnershipToken();
        var startInfo = CreateStartInfo(
            snapshot,
            fullProjectRoot,
            _actioHome,
            _url,
            instanceId,
            ownershipToken);

        DetachedProcessHandle detachedProcess;
        try
        {
            detachedProcess = DetachedProcessStarter.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            error.WriteLine($"Actio web UI worker could not be started: {ex.Message}");
            return null;
        }

        var process = detachedProcess.Process;
        using (process)
        {
            try
            {
                var metadata = WebProcessMetadata.Create(
                    process,
                    instanceId,
                    ownershipToken,
                    snapshot,
                    _url,
                    fullProjectRoot,
                    _actioHome);
                store.Save(metadata);
                store.AppendLog(
                    $"started pid={metadata.ProcessId} instance={metadata.InstanceId} runtime={metadata.RuntimeIdentity}");

                var deadline = DateTimeOffset.UtcNow + _startupTimeout;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        await WriteWorkerExitAsync(
                            error,
                            store,
                            process,
                            detachedProcess.CapturesOutput);
                        TryDeleteOwned(store, instanceId);
                        return null;
                    }

                    health = await GetHealthAsync(fullProjectRoot, snapshot.Identity, cancellationToken);
                    if (health.Status == WebServerHealth.Ready &&
                        IsExpectedWorker(health.Response, metadata))
                    {
                        return runUrl;
                    }

                    if (health.Status == WebServerHealth.DifferentContext)
                    {
                        WriteContextMismatch(error);
                        await StopStartedProcessAsync(process, metadata, store, cancellationToken);
                        return null;
                    }

                    await Task.Delay(150, cancellationToken);
                }

                error.WriteLine($"Actio web UI did not respond at '{_url}' before the startup timeout.");
                var lastLogLine = store.ReadLastLogLine();
                if (lastLogLine is not null)
                {
                    error.WriteLine($"Last web worker diagnostic: {lastLogLine}");
                }

                await StopStartedProcessAsync(process, metadata, store, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                TryDeleteOwned(store, instanceId);
                throw;
            }
            catch (Exception ex) when (IsRecoverableLifecycleError(ex))
            {
                TryKill(process);
                TryDeleteOwned(store, instanceId);
                error.WriteLine($"Actio web UI worker failed: {ex.Message}");
                return null;
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        WebRuntimeSnapshot snapshot,
        string projectRoot,
        string actioHome,
        string url,
        string instanceId,
        string ownershipToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = snapshot.HostPath,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        if (snapshot.UsesDotnetHost)
        {
            startInfo.ArgumentList.Add(snapshot.EntryAssemblyPath);
        }

        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--project-root");
        startInfo.ArgumentList.Add(projectRoot);
        startInfo.ArgumentList.Add("--actio-home");
        startInfo.ArgumentList.Add(actioHome);
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("--background");

        var inherited = Environment.GetEnvironmentVariables();
        startInfo.Environment.Clear();
        foreach (var name in PreservedEnvironmentVariables)
        {
            if (inherited[name] is string value)
            {
                startInfo.Environment[name] = value;
            }
        }

        startInfo.Environment[RuntimeIdentityEnvironmentVariable] = snapshot.Identity;
        startInfo.Environment[InstanceIdEnvironmentVariable] = instanceId;
        startInfo.Environment[ControlTokenEnvironmentVariable] = ownershipToken;
        startInfo.Environment[SnapshotPathEnvironmentVariable] = snapshot.RootPath;
        return startInfo;
    }

    private async Task<WebServerHealthResult> GetHealthAsync(
        string projectRoot,
        string runtimeIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = _httpClientFactory();
            http.Timeout = TimeSpan.FromMilliseconds(500);
            using var response = await http.GetAsync($"{_url}/api/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return WebServerHealthResult.Offline();
            }

            var health = await response.Content.ReadFromJsonAsync<WebHealthResponse>(cancellationToken);
            if (health is null ||
                !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return WebServerHealthResult.DifferentContext(health);
            }

            if (!IsSamePath(health.ProjectRoot, projectRoot) ||
                !IsSamePath(health.ActioHome, _actioHome))
            {
                return WebServerHealthResult.DifferentContext(health);
            }

            return string.Equals(health.RuntimeIdentity, runtimeIdentity, StringComparison.Ordinal)
                ? WebServerHealthResult.Ready(health)
                : WebServerHealthResult.Incompatible(health);
        }
        catch (HttpRequestException)
        {
            return WebServerHealthResult.Offline();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebServerHealthResult.Offline();
        }
        catch (NotSupportedException)
        {
            return WebServerHealthResult.DifferentContext(null);
        }
        catch (System.Text.Json.JsonException)
        {
            return WebServerHealthResult.DifferentContext(null);
        }
    }

    private async Task<bool> StopVerifiedProcessAsync(
        WebProcessMetadataStore store,
        WebHealthResponse? health,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var metadataResult = store.Read();
        var metadata = metadataResult.Metadata;
        if (metadata is null ||
            health is null ||
            !IsExpectedWorker(health, metadata) ||
            WebProcessMetadataStore.GetOwnerState(
                metadata.ProcessId,
                metadata.ProcessStartTimeUtcTicks) != WebOwnerState.Active)
        {
            error.WriteLine(
                "Actio web UI uses an incompatible runtime, but ownership could not be verified. Stop it manually before retrying.");
            return false;
        }

        try
        {
            using var http = _httpClientFactory();
            http.Timeout = _startupTimeout;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_url}/api/internal/shutdown");
            request.Headers.Add("X-Actio-Control-Token", metadata.OwnershipToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is not HttpStatusCode.Accepted)
            {
                error.WriteLine("Actio web UI refused the verified shutdown request.");
                return false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error.WriteLine($"Actio web UI shutdown request failed: {ex.Message}");
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (WebProcessMetadataStore.GetOwnerState(
                metadata.ProcessId,
                metadata.ProcessStartTimeUtcTicks) == WebOwnerState.Stale)
            {
                store.DeleteIfOwned(metadata.InstanceId);
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        if (WebProcessMetadataStore.GetOwnerState(
            metadata.ProcessId,
            metadata.ProcessStartTimeUtcTicks) != WebOwnerState.Active)
        {
            error.WriteLine("Actio web UI process state became unverifiable during shutdown.");
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(metadata.ProcessId);
            if (!IsVerifiedProcess(process, metadata))
            {
                error.WriteLine(
                    "Actio web UI process identity changed during shutdown. No process was stopped.");
                return false;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            store.DeleteIfOwned(metadata.InstanceId);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            error.WriteLine($"Actio web UI verified process could not be stopped: {ex.Message}");
            return false;
        }
    }

    private static async Task StopStartedProcessAsync(
        Process process,
        WebProcessMetadata metadata,
        WebProcessMetadataStore store,
        CancellationToken cancellationToken)
    {
        if (!process.HasExited &&
            process.StartTime.ToUniversalTime().Ticks == metadata.ProcessStartTimeUtcTicks)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }

        store.DeleteIfOwned(metadata.InstanceId);
    }

    private OfflineMetadataDecision HandleOfflineMetadata(
        WebProcessMetadataStore store,
        TextWriter error)
    {
        var result = store.Read();
        if (result.IsCorrupt)
        {
            try
            {
                var quarantinePath = store.QuarantineCorrupt();
                error.WriteLine(
                    $"Actio web UI metadata was corrupt and moved to '{quarantinePath}': {result.Error}");
                return OfflineMetadataDecision.ProceedWithoutSnapshotCleanup;
            }
            catch (Exception ex) when (IsRecoverableLifecycleError(ex))
            {
                error.WriteLine($"Actio web UI corrupt metadata could not be quarantined: {ex.Message}");
                return OfflineMetadataDecision.Block;
            }
        }

        if (result.Metadata is null)
        {
            return OfflineMetadataDecision.Proceed;
        }

        return WebProcessMetadataStore.GetOwnerState(
            result.Metadata.ProcessId,
            result.Metadata.ProcessStartTimeUtcTicks) switch
        {
            WebOwnerState.Stale => DeleteStaleMetadata(store, result.Metadata, error),
            WebOwnerState.Active => WriteOfflineActiveOwner(error),
            _ => WriteUnverifiableOwner(error)
        };
    }

    private static OfflineMetadataDecision DeleteStaleMetadata(
        WebProcessMetadataStore store,
        WebProcessMetadata metadata,
        TextWriter error)
    {
        try
        {
            store.DeleteIfOwned(metadata.InstanceId);
            return OfflineMetadataDecision.Proceed;
        }
        catch (Exception ex) when (IsRecoverableLifecycleError(ex))
        {
            error.WriteLine($"Actio web UI stale metadata could not be removed: {ex.Message}");
            return OfflineMetadataDecision.Block;
        }
    }

    private static OfflineMetadataDecision WriteOfflineActiveOwner(TextWriter error)
    {
        error.WriteLine(
            "Actio web UI metadata belongs to an active process that is not responding. It was not stopped; stop it manually before retrying.");
        return OfflineMetadataDecision.Block;
    }

    private static OfflineMetadataDecision WriteUnverifiableOwner(TextWriter error)
    {
        error.WriteLine(
            "Actio web UI process ownership could not be verified. No process was stopped; inspect the local process metadata before retrying.");
        return OfflineMetadataDecision.Block;
    }

    private static bool TryGetProtectedRuntimeIdentities(
        WebProcessMetadataStore store,
        out IReadOnlySet<string> protectedRuntimeIdentities)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<WebProcessMetadataReadResult> results;
        try
        {
            results = store.ReadAll();
        }
        catch (Exception ex) when (IsRecoverableLifecycleError(ex))
        {
            protectedRuntimeIdentities = identities;
            return false;
        }

        foreach (var result in results)
        {
            if (result.IsCorrupt)
            {
                protectedRuntimeIdentities = identities;
                return false;
            }

            var metadata = result.Metadata;
            if (metadata is null)
            {
                continue;
            }

            var ownerState = WebProcessMetadataStore.GetOwnerState(
                metadata.ProcessId,
                metadata.ProcessStartTimeUtcTicks);
            if (ownerState is WebOwnerState.Active or WebOwnerState.Unknown)
            {
                identities.Add(metadata.RuntimeIdentity);
            }
        }

        protectedRuntimeIdentities = identities;
        return true;
    }

    private void CleanupSnapshots(
        TextWriter error,
        string currentIdentity,
        IReadOnlySet<string> protectedRuntimeIdentities)
    {
        try
        {
            WriteCleanupWarnings(
                error,
                _snapshotManager.Cleanup(
                    _actioHome,
                    currentIdentity,
                    protectedRuntimeIdentities,
                    TimeSpan.FromMinutes(10)));
        }
        catch (Exception ex) when (IsRecoverableLifecycleError(ex))
        {
            error.WriteLine($"Actio web UI snapshot cleanup was skipped: {ex.Message}");
        }
    }

    private static bool IsExpectedWorker(
        WebHealthResponse? health,
        WebProcessMetadata metadata)
    {
        return health is not null &&
            health.ProcessId == metadata.ProcessId &&
            health.ProcessStartTimeUtcTicks == metadata.ProcessStartTimeUtcTicks &&
            string.Equals(health.WebInstanceId, metadata.InstanceId, StringComparison.Ordinal) &&
            string.Equals(health.RuntimeIdentity, metadata.RuntimeIdentity, StringComparison.Ordinal) &&
            string.Equals(
                health.ServerUrl?.TrimEnd('/'),
                metadata.Url.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase) &&
            IsSamePath(health.ProjectRoot, metadata.ProjectRoot) &&
            IsSamePath(health.ActioHome, metadata.ActioHome);
    }

    internal static bool IsVerifiedProcess(Process process, WebProcessMetadata metadata)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks == metadata.ProcessStartTimeUtcTicks &&
                IsSamePath(process.MainModule?.FileName, metadata.HostPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    private void WriteContextMismatch(TextWriter error)
    {
        error.WriteLine(
            $"Actio web UI is already running at '{_url}', but it uses a different project root or ACTIO_HOME.");
        error.WriteLine("Stop that process or start Actio web with a different --url.");
    }

    private static void WriteCleanupWarnings(TextWriter error, WebSnapshotCleanupResult cleanup)
    {
        foreach (var warning in cleanup.Warnings)
        {
            error.WriteLine($"Actio web UI cleanup warning: {warning}");
        }
    }

    private static async Task WriteWorkerExitAsync(
        TextWriter error,
        WebProcessMetadataStore store,
        Process process,
        bool capturesOutput)
    {
        if (capturesOutput)
        {
            var standardError = await process.StandardError.ReadToEndAsync();
            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            foreach (var line in standardError
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Concat(standardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)))
            {
                store.AppendLog($"startup output: {line}");
            }
        }

        error.WriteLine($"Actio web UI worker exited during startup with code {process.ExitCode}.");
        var lastLogLine = store.ReadLastLogLine();
        if (lastLogLine is not null)
        {
            error.WriteLine($"Last web worker diagnostic: {lastLogLine}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
        }
    }

    private static void TryDeleteOwned(WebProcessMetadataStore store, string instanceId)
    {
        try
        {
            store.DeleteIfOwned(instanceId);
        }
        catch (Exception ex) when (IsRecoverableLifecycleError(ex))
        {
        }
    }

    private static bool IsSamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static bool IsRecoverableLifecycleError(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException
            or System.Text.Json.JsonException;
    }

    private enum WebServerHealth
    {
        Offline,
        Ready,
        DifferentContext,
        IncompatibleRuntime
    }

    private enum OfflineMetadataDecision
    {
        Proceed,
        ProceedWithoutSnapshotCleanup,
        Block
    }

    private sealed record WebServerHealthResult(
        WebServerHealth Status,
        WebHealthResponse? Response)
    {
        public static WebServerHealthResult Offline() => new(WebServerHealth.Offline, null);

        public static WebServerHealthResult Ready(WebHealthResponse response) => new(WebServerHealth.Ready, response);

        public static WebServerHealthResult DifferentContext(WebHealthResponse? response) =>
            new(WebServerHealth.DifferentContext, response);

        public static WebServerHealthResult Incompatible(WebHealthResponse response) =>
            new(WebServerHealth.IncompatibleRuntime, response);
    }

    private sealed record WebHealthResponse(
        string? Status,
        string? ProjectRoot,
        string? ActioHome,
        string? ServerUrl,
        string? RuntimeIdentity,
        string? WebInstanceId,
        int? ProcessId,
        long? ProcessStartTimeUtcTicks);
}
