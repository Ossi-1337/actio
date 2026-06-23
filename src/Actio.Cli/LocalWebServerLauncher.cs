using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using Actio.Storage;
using Actio.Web;

namespace Actio.Cli;

public sealed class LocalWebServerLauncher : ILocalWebServerLauncher
{
    private readonly string _url;
    private readonly string _actioHome;
    private readonly TimeSpan _startupTimeout;

    public LocalWebServerLauncher()
        : this(ActioWebDefaults.DefaultUrl, ActioHome.Resolve(), TimeSpan.FromSeconds(3))
    {
    }

    public LocalWebServerLauncher(string url, string actioHome, TimeSpan startupTimeout)
    {
        _url = url.TrimEnd('/');
        _actioHome = actioHome;
        _startupTimeout = startupTimeout;
    }

    public async Task<string?> EnsureStartedAsync(
        string projectRoot,
        string? runId,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var runUrl = runId is null
            ? _url
            : $"{_url}/runs/{Uri.EscapeDataString(runId)}";

        var health = await GetHealthAsync(projectRoot, cancellationToken);
        if (health == WebServerHealth.Ready)
        {
            return runUrl;
        }

        if (health == WebServerHealth.DifferentContext)
        {
            WriteContextMismatch(error);
            return null;
        }

        if (!TryStart(projectRoot, error))
        {
            return runUrl;
        }

        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            health = await GetHealthAsync(projectRoot, cancellationToken);
            if (health == WebServerHealth.Ready)
            {
                return runUrl;
            }

            if (health == WebServerHealth.DifferentContext)
            {
                WriteContextMismatch(error);
                return null;
            }

            await Task.Delay(150, cancellationToken);
        }

        error.WriteLine($"Actio web UI did not respond at '{_url}' before the startup timeout.");
        return runUrl;
    }

    private async Task<WebServerHealth> GetHealthAsync(string projectRoot, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(500)
            };
            using var response = await http.GetAsync($"{_url}/api/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return WebServerHealth.Offline;
            }

            var health = await response.Content.ReadFromJsonAsync<WebHealthResponse>(cancellationToken);
            return health is not null &&
                string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
                IsSamePath(health.ProjectRoot, projectRoot) &&
                IsSamePath(health.ActioHome, _actioHome)
                    ? WebServerHealth.Ready
                    : WebServerHealth.DifferentContext;
        }
        catch (HttpRequestException)
        {
            return WebServerHealth.Offline;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebServerHealth.Offline;
        }
        catch (NotSupportedException)
        {
            return WebServerHealth.DifferentContext;
        }
        catch (System.Text.Json.JsonException)
        {
            return WebServerHealth.DifferentContext;
        }
    }

    private void WriteContextMismatch(TextWriter error)
    {
        error.WriteLine(
            $"Actio web UI is already running at '{_url}', but it uses a different project root or ACTIO_HOME.");
        error.WriteLine("Stop that process or start Actio web with a different --url.");
    }

    private bool TryStart(string projectRoot, TextWriter error)
    {
        var startInfo = CreateStartInfo(projectRoot);
        if (startInfo is null)
        {
            error.WriteLine("Actio web UI could not be started because the current executable path was not found.");
            return false;
        }

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"Actio web UI could not be started: {ex.Message}");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            error.WriteLine($"Actio web UI could not be started: {ex.Message}");
            return false;
        }
    }

    private ProcessStartInfo? CreateStartInfo(string projectRoot)
    {
        var processPath = Environment.ProcessPath;
        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (processPath is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (IsDotnetHost(processPath))
        {
            if (assemblyPath is null)
            {
                return null;
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--project-root");
        startInfo.ArgumentList.Add(projectRoot);
        startInfo.ArgumentList.Add("--actio-home");
        startInfo.ArgumentList.Add(_actioHome);
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(_url);
        startInfo.Environment["ACTIO_HOME"] = _actioHome;

        return startInfo;
    }

    private static bool IsDotnetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
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

    private enum WebServerHealth
    {
        Offline,
        Ready,
        DifferentContext
    }

    private sealed record WebHealthResponse(string? Status, string? ProjectRoot, string? ActioHome);
}
