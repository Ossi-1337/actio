using System.ComponentModel;
using System.Diagnostics;
using Actio.Engine.Execution;

namespace Actio.Runner.Docker;

public sealed class DockerRunnerProvider : IRunnerProvider
{
    private readonly DockerImageResolver _imageResolver;

    public DockerRunnerProvider()
        : this(new DockerImageResolver())
    {
    }

    public DockerRunnerProvider(DockerImageResolver imageResolver)
    {
        _imageResolver = imageResolver;
    }

    public bool SupportsRunner(string runsOn)
    {
        return _imageResolver.TryResolveImage(runsOn, out _);
    }

    public async Task<StepExecutionResult> ExecuteStepAsync(
        StepExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveStepImage(request, out var image))
        {
            var message = $"Runner '{request.RunsOn}' is not mapped to a Docker image.";
            await output.WriteErrorLineAsync(message, cancellationToken);
            return new StepExecutionResult(1);
        }

        var containerName = CreateContainerName(request.JobName, request.StepName);
        using var process = new Process
        {
            StartInfo = CreateShellStepStartInfo(request, image, containerName),
            EnableRaisingEvents = true
        };

        return await ExecuteDockerProcessAsync(process, containerName, output, cancellationToken);
    }

    private bool TryResolveStepImage(StepExecutionRequest request, out string image)
    {
        if (request.Container is not null)
        {
            image = request.Container.Image;
            return true;
        }

        return _imageResolver.TryResolveImage(request.RunsOn, out image!);
    }

    public async Task<StepExecutionResult> ExecuteDockerActionAsync(
        DockerActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        var containerName = CreateContainerName(request.JobName, request.StepName);
        using var process = new Process
        {
            StartInfo = CreateDockerActionStartInfo(request, containerName),
            EnableRaisingEvents = true
        };

        return await ExecuteDockerProcessAsync(process, containerName, output, cancellationToken);
    }

    private static async Task<StepExecutionResult> ExecuteDockerProcessAsync(
        Process process,
        string containerName,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!process.Start())
            {
                const string message = "Docker process could not be started.";
                await output.WriteErrorLineAsync(message, cancellationToken);
                return new StepExecutionResult(1);
            }
        }
        catch (Win32Exception ex)
        {
            var message = $"Docker could not be started: {ex.Message}";
            await output.WriteErrorLineAsync(message, cancellationToken);
            return new StepExecutionResult(1);
        }

        var outputTask = RedirectOutputLinesAsync(process.StandardOutput, output, cancellationToken);
        var errorTask = RedirectErrorLinesAsync(process.StandardError, output, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            TryRemoveContainer(containerName);
            throw;
        }

        return new StepExecutionResult(process.ExitCode);
    }

    internal static ProcessStartInfo CreateShellStepStartInfo(StepExecutionRequest request, string image, string containerName)
    {
        var startInfo = CreateBaseStartInfo(
            request.JobName,
            request.StepName,
            request.ProjectRoot,
            request.Environment,
            containerName,
            request.WorkingDirectory,
            request.AdditionalMounts,
            request.Container);
        startInfo.ArgumentList.Add(image);
        startInfo.ArgumentList.Add(NormalizeShell(request.Shell));
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(BuildShellScript(request.Command));

        return startInfo;
    }

    internal static ProcessStartInfo CreateDockerActionStartInfo(
        DockerActionExecutionRequest request,
        string containerName)
    {
        var startInfo = CreateBaseStartInfo(
            request.JobName,
            request.StepName,
            request.ProjectRoot,
            request.Environment,
            containerName,
            null,
            request.AdditionalMounts,
            null);
        startInfo.ArgumentList.Add(request.Image);
        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(
        string jobName,
        string stepName,
        string projectRoot,
        IReadOnlyDictionary<string, string> environment,
        string containerName,
        string? workingDirectory,
        IReadOnlyList<StepExecutionMount>? additionalMounts,
        JobContainerExecutionOptions? container)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--rm");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add("actio=true");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.job={jobName}");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.step={stepName}");

        foreach (var port in container?.Ports ?? [])
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(port);
        }

        foreach (var option in container?.Options ?? [])
        {
            startInfo.ArgumentList.Add(option);
        }

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add($"{Path.GetFullPath(projectRoot)}:/workspace");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add(ToContainerWorkingDirectory(workingDirectory));

        foreach (var mount in container?.Volumes ?? [])
        {
            AddMount(startInfo, mount);
        }

        foreach (var mount in additionalMounts ?? [])
        {
            AddMount(startInfo, mount);
        }

        foreach (var (key, value) in environment.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"{key}={value}");
        }

        return startInfo;
    }

    private static void AddMount(ProcessStartInfo startInfo, StepExecutionMount mount)
    {
        var suffix = mount.ReadOnly ? ":ro" : string.Empty;
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add($"{Path.GetFullPath(mount.HostPath)}:{mount.ContainerPath}{suffix}");
    }

    private static string NormalizeShell(string? shell)
        => string.IsNullOrWhiteSpace(shell) ? "sh" : shell;

    internal static string ToContainerWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return "/workspace";
        }

        var normalized = workingDirectory.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized)
            ? "/workspace"
            : $"/workspace/{normalized}";
    }

    internal static string BuildShellScript(string command)
    {
        return $"""
            set -e
            if (set -o pipefail) 2>/dev/null; then
              set -o pipefail
            fi
            {command}
            """;
    }

    private static async Task RedirectOutputLinesAsync(
        TextReader reader,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await output.WriteOutputLineAsync(line, cancellationToken);
        }
    }

    private static async Task RedirectErrorLinesAsync(
        TextReader reader,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await output.WriteErrorLineAsync(line, cancellationToken);
        }
    }

    private static string CreateContainerName(string jobName, string stepName)
    {
        var name = $"actio-{SanitizeName(jobName)}-{SanitizeName(stepName)}-{Guid.NewGuid():N}";
        return name.Length <= 63 ? name : name[..63].TrimEnd('-');
    }

    private static string SanitizeName(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        var sanitized = new string(chars).Trim('-');

        return string.IsNullOrWhiteSpace(sanitized) ? "step" : sanitized;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void TryRemoveContainer(string containerName)
    {
        try
        {
            using var cleanup = Process.Start(new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "rm",
                    "-f",
                    containerName
                }
            });

            cleanup?.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
