using System.ComponentModel;
using System.Diagnostics;
using Actio.Engine.Execution;

namespace Actio.Runner.Docker;

public sealed class DockerRunnerProvider : IRunnerProvider
{
    private const string JavaScriptActionNodeImage = "node:20-bookworm-slim";

    private static readonly TimeSpan ServiceHealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ServiceHealthPollInterval = TimeSpan.FromMilliseconds(500);

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

    public async Task<ServiceContainerStartResult> StartServiceContainersAsync(
        ServiceContainerStartRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Services.Count == 0)
        {
            return ServiceContainerStartResult.Started(null);
        }

        var errors = new List<string>();
        var containerNames = new List<string>();
        var networkName = CreateNetworkName(request.JobName);

        try
        {
            var networkResult = await RunDockerCommandAsync(
                CreateNetworkCreateStartInfo(request.JobName, networkName),
                cancellationToken);

            if (!networkResult.Success)
            {
                return ServiceContainerStartResult.Failed(
                    [FormatDockerCommandError($"creating service network for job '{request.JobName}'", networkResult)]);
            }

            foreach (var service in request.Services)
            {
                var containerName = CreateServiceContainerName(request.JobName, service.Name);
                containerNames.Add(containerName);
                var startResult = await RunDockerCommandAsync(
                    CreateServiceContainerStartInfo(request, service, networkName, containerName),
                    cancellationToken);

                if (!startResult.Success)
                {
                    errors.Add(FormatDockerCommandError($"starting service '{service.Name}'", startResult));
                    break;
                }

                errors.AddRange(await WaitForServiceHealthAsync(service.Name, containerName, cancellationToken));
                if (errors.Count > 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await StopServiceContainersAsync(new JobServiceNetwork(networkName, containerNames), CancellationToken.None);
            throw;
        }

        if (errors.Count > 0)
        {
            var stopResult = await StopServiceContainersAsync(
                new JobServiceNetwork(networkName, containerNames),
                CancellationToken.None);
            errors.AddRange(stopResult.Errors);
            return ServiceContainerStartResult.Failed(errors);
        }

        return ServiceContainerStartResult.Started(new JobServiceNetwork(networkName, containerNames));
    }

    public async Task<ServiceContainerStopResult> StopServiceContainersAsync(
        JobServiceNetwork network,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (var containerName in network.ContainerNames)
        {
            var removeResult = await RunDockerCommandAsync(
                CreateContainerRemoveStartInfo(containerName),
                cancellationToken);
            if (!removeResult.Success)
            {
                errors.Add(FormatDockerCommandError($"removing service container '{containerName}'", removeResult));
            }
        }

        var networkRemoveResult = await RunDockerCommandAsync(
            CreateNetworkRemoveStartInfo(network.NetworkName),
            cancellationToken);
        if (!networkRemoveResult.Success)
        {
            errors.Add(FormatDockerCommandError($"removing service network '{network.NetworkName}'", networkRemoveResult));
        }

        return new ServiceContainerStopResult(errors);
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

    public async Task<StepExecutionResult> ExecuteDockerfileActionAsync(
        DockerfileActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        var buildResult = await EnsureDockerfileActionImageAsync(request, output, cancellationToken);
        if (!buildResult.Success)
        {
            return buildResult;
        }

        return await ExecuteDockerActionAsync(
            new DockerActionExecutionRequest(
                request.JobName,
                request.StepName,
                request.Image,
                request.ProjectRoot,
                request.Environment,
                request.AdditionalMounts,
                request.Services,
                request.EntryPoint,
                request.Arguments),
            output,
            cancellationToken);
    }

    private static async Task<StepExecutionResult> EnsureDockerfileActionImageAsync(
        DockerfileActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        var inspectResult = await RunDockerCommandAsync(
            CreateImageInspectStartInfo(request.Image),
            cancellationToken);

        if (inspectResult.Success)
        {
            return new StepExecutionResult(0);
        }

        using var process = new Process
        {
            StartInfo = CreateDockerfileActionBuildStartInfo(request),
            EnableRaisingEvents = true
        };

        return await ExecuteDockerProcessAsync(process, null, output, cancellationToken);
    }

    public async Task<StepExecutionResult> ExecuteJavaScriptActionAsync(
        JavaScriptActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        var result = new StepExecutionResult(0);

        if (!string.IsNullOrWhiteSpace(request.Pre))
        {
            result = await ExecuteJavaScriptActionPhaseAsync(request, request.Pre, "pre", output, cancellationToken);
        }

        if (result.Success)
        {
            result = await ExecuteJavaScriptActionPhaseAsync(request, request.Main, "main", output, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.Post))
        {
            var postResult = await ExecuteJavaScriptActionPhaseAsync(request, request.Post, "post", output, cancellationToken);
            if (result.Success)
            {
                result = postResult;
            }
        }

        return result;
    }

    private static async Task<StepExecutionResult> ExecuteJavaScriptActionPhaseAsync(
        JavaScriptActionExecutionRequest request,
        string scriptPath,
        string phase,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        var containerName = CreateContainerName(request.JobName, $"{request.StepName}-{phase}");
        using var process = new Process
        {
            StartInfo = CreateJavaScriptActionStartInfo(request, scriptPath, containerName),
            EnableRaisingEvents = true
        };

        return await ExecuteDockerProcessAsync(process, containerName, output, cancellationToken);
    }

    private static async Task<StepExecutionResult> ExecuteDockerProcessAsync(
        Process process,
        string? containerName,
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
            if (containerName is not null)
            {
                TryRemoveContainer(containerName);
            }

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
            request.Container,
            request.Services);
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
            null,
            request.Services);
        if (!string.IsNullOrWhiteSpace(request.EntryPoint))
        {
            startInfo.ArgumentList.Add("--entrypoint");
            startInfo.ArgumentList.Add(request.EntryPoint);
        }

        startInfo.ArgumentList.Add(request.Image);
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static ProcessStartInfo CreateDockerfileActionBuildStartInfo(
        DockerfileActionExecutionRequest request)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add("actio=true");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.job={request.JobName}");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.step={request.StepName}");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(request.Image);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(Path.GetFullPath(request.DockerfilePath));
        startInfo.ArgumentList.Add(Path.GetFullPath(request.BuildContext));
        return startInfo;
    }

    internal static ProcessStartInfo CreateJavaScriptActionStartInfo(
        JavaScriptActionExecutionRequest request,
        string scriptPath,
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
            null,
            request.Services);

        startInfo.ArgumentList.Add(JavaScriptActionNodeImage);
        startInfo.ArgumentList.Add("node");
        startInfo.ArgumentList.Add(ToActionContainerPath(request.ActionPath, scriptPath));

        return startInfo;
    }

    internal static ProcessStartInfo CreateServiceContainerStartInfo(
        ServiceContainerStartRequest request,
        ServiceContainerDefinition service,
        string networkName,
        string containerName)
    {
        var startInfo = CreateDockerStartInfo();

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add("actio=true");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.job={request.JobName}");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.service={service.Name}");
        startInfo.ArgumentList.Add("--network");
        startInfo.ArgumentList.Add(networkName);
        startInfo.ArgumentList.Add("--network-alias");
        startInfo.ArgumentList.Add(service.Name);

        foreach (var port in service.Ports)
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(port);
        }

        foreach (var option in service.Options)
        {
            startInfo.ArgumentList.Add(option);
        }

        foreach (var mount in service.Volumes)
        {
            AddMount(startInfo, mount);
        }

        foreach (var (key, value) in service.Environment.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"{key}={value}");
        }

        startInfo.ArgumentList.Add(service.Image);
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
        JobContainerExecutionOptions? container,
        JobServiceNetwork? services)
    {
        var startInfo = CreateDockerStartInfo();

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

        if (services is not null)
        {
            startInfo.ArgumentList.Add("--network");
            startInfo.ArgumentList.Add(services.NetworkName);
        }

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

    private static ProcessStartInfo CreateDockerStartInfo()
    {
        return new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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

    internal static string ToActionContainerPath(string actionPath, string scriptPath)
    {
        var normalizedActionPath = actionPath.Replace('\\', '/').TrimEnd('/');
        var normalizedScriptPath = scriptPath.Replace('\\', '/').TrimStart('/');
        if (normalizedScriptPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedScriptPath = normalizedScriptPath[2..];
        }

        return $"{normalizedActionPath}/{normalizedScriptPath}";
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

    private static ProcessStartInfo CreateNetworkCreateStartInfo(string jobName, string networkName)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("network");
        startInfo.ArgumentList.Add("create");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add("actio=true");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.job={jobName}");
        startInfo.ArgumentList.Add(networkName);
        return startInfo;
    }

    private static ProcessStartInfo CreateNetworkRemoveStartInfo(string networkName)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("network");
        startInfo.ArgumentList.Add("rm");
        startInfo.ArgumentList.Add(networkName);
        return startInfo;
    }

    private static ProcessStartInfo CreateContainerRemoveStartInfo(string containerName)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("rm");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(containerName);
        return startInfo;
    }

    private static ProcessStartInfo CreateContainerHealthInspectStartInfo(string containerName)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}");
        startInfo.ArgumentList.Add(containerName);
        return startInfo;
    }

    private static ProcessStartInfo CreateContainerLogsStartInfo(string containerName)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("logs");
        startInfo.ArgumentList.Add("--tail");
        startInfo.ArgumentList.Add("50");
        startInfo.ArgumentList.Add(containerName);
        return startInfo;
    }

    private static ProcessStartInfo CreateImageInspectStartInfo(string image)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("image");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add(image);
        return startInfo;
    }

    private static async Task<IReadOnlyList<string>> WaitForServiceHealthAsync(
        string serviceName,
        string containerName,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ServiceHealthTimeout;
        while (true)
        {
            var healthResult = await RunDockerCommandAsync(
                CreateContainerHealthInspectStartInfo(containerName),
                cancellationToken);
            if (!healthResult.Success)
            {
                return [FormatDockerCommandError($"checking health for service '{serviceName}'", healthResult)];
            }

            var status = healthResult.StandardOutput.Trim();
            if (string.Equals(status, "none", StringComparison.Ordinal) ||
                string.Equals(status, "healthy", StringComparison.Ordinal))
            {
                return [];
            }

            if (string.Equals(status, "unhealthy", StringComparison.Ordinal))
            {
                return await CreateServiceHealthErrorsAsync(
                    serviceName,
                    containerName,
                    $"Service '{serviceName}' became unhealthy.",
                    cancellationToken);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return await CreateServiceHealthErrorsAsync(
                    serviceName,
                    containerName,
                    $"Service '{serviceName}' did not become healthy within {ServiceHealthTimeout.TotalSeconds:0} second(s).",
                    cancellationToken);
            }

            await Task.Delay(ServiceHealthPollInterval, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<string>> CreateServiceHealthErrorsAsync(
        string serviceName,
        string containerName,
        string message,
        CancellationToken cancellationToken)
    {
        var errors = new List<string> { message };
        var logsResult = await RunDockerCommandAsync(CreateContainerLogsStartInfo(containerName), cancellationToken);
        if (logsResult.Success)
        {
            AddDockerOutput(errors, $"Service '{serviceName}' logs", logsResult);
        }
        else
        {
            errors.Add(FormatDockerCommandError($"reading logs for service '{serviceName}'", logsResult));
        }

        return errors;
    }

    private static async Task<DockerCommandResult> RunDockerCommandAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return new DockerCommandResult(1, string.Empty, "Docker process could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            return new DockerCommandResult(1, string.Empty, $"Docker could not be started: {ex.Message}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }

        return new DockerCommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static string FormatDockerCommandError(string action, DockerCommandResult result)
    {
        var errors = new List<string> { $"Docker failed while {action} with exit code {result.ExitCode}." };
        AddDockerOutput(errors, "stdout", result.StandardOutput);
        AddDockerOutput(errors, "stderr", result.StandardError);
        return string.Join(Environment.NewLine, errors);
    }

    private static void AddDockerOutput(List<string> errors, string label, DockerCommandResult result)
    {
        AddDockerOutput(errors, label, result.StandardOutput);
        AddDockerOutput(errors, label, result.StandardError);
    }

    private static void AddDockerOutput(List<string> errors, string label, string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length > 0)
        {
            errors.Add($"{label}: {trimmed}");
        }
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

    private static string CreateServiceContainerName(string jobName, string serviceName)
    {
        var name = $"actio-{SanitizeName(jobName)}-{SanitizeName(serviceName)}-{Guid.NewGuid():N}";
        return name.Length <= 63 ? name : name[..63].TrimEnd('-');
    }

    private static string CreateNetworkName(string jobName)
    {
        var name = $"actio-{SanitizeName(jobName)}-net-{Guid.NewGuid():N}";
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
            using var cleanup = Process.Start(CreateContainerRemoveStartInfo(containerName));

            cleanup?.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed record DockerCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public bool Success => ExitCode == 0;
    }
}
