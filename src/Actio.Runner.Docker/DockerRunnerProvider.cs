using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Actio.Core.Workflows;
using Actio.Engine.Execution;
using Actio.Engine.Runs;

namespace Actio.Runner.Docker;

public sealed class DockerRunnerProvider : IRunnerProvider
{
    private const string JavaScriptActionNodeImage = "node:20-bookworm-slim";

    private static readonly TimeSpan ServiceHealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ServiceHealthPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly DockerImageResolver _imageResolver;
    private readonly ConcurrentDictionary<string, RunnerImageUserObservation> _imageUserObservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string ConfiguredUser, string Status)> _imageUsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warnedRootImages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunnerNetworkObservation> _networkObservations = new(StringComparer.Ordinal);
    private readonly DockerPortLeaseManager _portLeases = new();

    public DockerRunnerProvider()
        : this(new DockerImageResolver())
    {
    }

    public DockerRunnerProvider(DockerImageResolver imageResolver)
    {
        _imageResolver = imageResolver;
    }

    public RunnerSecurityMetadata SecurityMetadata
    {
        get
        {
            var observations = _imageUserObservations.Values
                .OrderBy(item => item.Surface, StringComparer.Ordinal)
                .ThenBy(item => item.Image, StringComparer.Ordinal)
                .ToArray();
            var degraded = DockerRuntimeSecurityPolicy.Metadata.DegradedControls.ToList();
            if (observations.Any(item => item.Status == "unknown"))
            {
                degraded.Add("one-or-more-image-users-not-evaluated");
            }

            var networkObservations = _networkObservations.Values
                .OrderBy(item => item.JobName, StringComparer.Ordinal)
                .ThenBy(item => item.NetworkName, StringComparer.Ordinal)
                .ToArray();
            if (networkObservations.Any(item => item.PublishedPorts.Count > 0))
            {
                degraded.Add("published-port-daemon-routing-not-verified");
            }

            return DockerRuntimeSecurityPolicy.Metadata with
            {
                DegradedControls = degraded,
                ImageUserObservations = observations,
                NetworkObservations = networkObservations
            };
        }
    }

    public bool SupportsRunner(string runsOn)
    {
        return _imageResolver.TryResolveImage(runsOn, out _);
    }

    public async Task<JobRuntimeStartResult> StartJobRuntimeAsync(
        JobRuntimeStartRequest request,
        CancellationToken cancellationToken = default)
    {
        foreach (var service in request.Services)
        {
            var policyError = DockerRuntimeSecurityPolicy.Validate(
                service.Options,
                service.Volumes,
                $"service '{service.Name}'");
            if (policyError is not null)
            {
                return JobRuntimeStartResult.Failed([policyError]);
            }

            var filesystemError = DockerRuntimeSecurityPolicy.ValidateFilesystem(
                request.ProjectRoot,
                service.Volumes,
                $"service '{service.Name}'");
            if (filesystemError is not null)
            {
                return JobRuntimeStartResult.Failed([filesystemError]);
            }
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        var containerNames = new List<string>();
        var reservedPorts = request.JobContainerPorts
            .Concat(request.Services.SelectMany(service => service.Ports))
            .Where(port => port.HostPort is not null)
            .ToArray();
        if (!_portLeases.TryAcquire(request.JobName, reservedPorts, out var leaseError))
        {
            return JobRuntimeStartResult.Failed([leaseError!]);
        }

        var networkName = CreateNetworkName(request.JobName);
        var runtime = new JobRuntimeContext(networkName, containerNames, reservedPorts, request.JobName);

        try
        {
            var networkResult = await RunDockerCommandAsync(
                CreateNetworkCreateStartInfo(request.JobName, networkName),
                cancellationToken);

            if (!networkResult.Success)
            {
                _portLeases.Release(request.JobName, reservedPorts);
                return JobRuntimeStartResult.Failed(
                    [FormatDockerCommandError($"creating job network for job '{request.JobName}'", networkResult)]);
            }

            _networkObservations[networkName] = CreateNetworkObservation(request, networkName);

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

                var observation = await ObserveImageUserAsync(
                    service.Image,
                    $"service:{service.Name}",
                    cancellationToken);
                if (observation.Status == "root" && _warnedRootImages.TryAdd(observation.Image, 0))
                {
                    warnings.Add(CreateRootUserWarning(observation));
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
            await StopJobRuntimeAsync(runtime, CancellationToken.None);
            throw;
        }
        catch
        {
            await StopJobRuntimeAsync(runtime, CancellationToken.None);
            throw;
        }

        if (errors.Count > 0)
        {
            var stopResult = await StopJobRuntimeAsync(runtime, CancellationToken.None);
            errors.AddRange(stopResult.Errors);
            return JobRuntimeStartResult.Failed(errors);
        }

        return JobRuntimeStartResult.Started(runtime, warnings);
    }

    public async Task<JobRuntimeStopResult> StopJobRuntimeAsync(
        JobRuntimeContext runtime,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        try
        {
            foreach (var containerName in runtime.ServiceContainerNames)
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
                CreateNetworkRemoveStartInfo(runtime.NetworkName),
                cancellationToken);
            if (!networkRemoveResult.Success)
            {
                errors.Add(FormatDockerCommandError($"removing job network '{runtime.NetworkName}'", networkRemoveResult));
            }
        }
        finally
        {
            if (runtime.PortLeaseOwner is not null)
            {
                _portLeases.Release(runtime.PortLeaseOwner, runtime.ReservedPorts);
            }
        }

        return new JobRuntimeStopResult(errors);
    }

    public async Task<StepExecutionResult> ExecuteStepAsync(
        StepExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        var policyError = DockerRuntimeSecurityPolicy.Validate(
            request.Container?.Options ?? [],
            (request.Container?.Volumes ?? []).Concat(request.AdditionalMounts),
            $"job '{request.JobName}' step '{request.StepName}'");
        if (policyError is not null)
        {
            await output.WriteErrorLineAsync(policyError, cancellationToken);
            return new StepExecutionResult(1);
        }

        var stepMounts = (request.Container?.Volumes ?? []).Concat(request.AdditionalMounts).ToArray();
        var filesystemError = DockerRuntimeSecurityPolicy.ValidateFilesystem(
            request.ProjectRoot,
            stepMounts,
            $"job '{request.JobName}' step '{request.StepName}'");
        if (filesystemError is not null)
        {
            await output.WriteErrorLineAsync(filesystemError, cancellationToken);
            return new StepExecutionResult(1);
        }

        if (!TryResolveStepImage(request, out var image))
        {
            var message = $"Runner '{request.RunsOn}' is not mapped to a Docker image.";
            await output.WriteErrorLineAsync(message, cancellationToken);
            return new StepExecutionResult(1);
        }

        var containerName = CreateContainerName(request.JobName, request.StepName);
        var observation = await ObserveImageUserAsync(
            image,
            $"shell:{request.JobName}/{request.StepName}",
            cancellationToken);
        await WriteRootWarningAsync(observation, output, cancellationToken);
        using var process = new Process
        {
            StartInfo = CreateShellStepStartInfo(request, image, containerName),
            EnableRaisingEvents = true
        };

        var result = await ExecuteDockerProcessAsync(process, containerName, output, cancellationToken);
        if (observation.Status == "unknown" && result.Success)
        {
            observation = await ObserveImageUserAsync(
                image,
                $"shell:{request.JobName}/{request.StepName}",
                cancellationToken);
            await WriteRootWarningAsync(observation, output, cancellationToken);
        }

        return result;
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

    public Task<StepExecutionResult> ExecuteDockerActionAsync(
        DockerActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
        => ExecuteDockerActionCoreAsync(request, output, "docker-action", cancellationToken);

    private async Task<StepExecutionResult> ExecuteDockerActionCoreAsync(
        DockerActionExecutionRequest request,
        IStepOutputSink output,
        string surfaceKind,
        CancellationToken cancellationToken)
    {
        var policyError = DockerRuntimeSecurityPolicy.Validate(
            [],
            request.AdditionalMounts,
            $"Docker action '{request.StepName}'");
        if (policyError is not null)
        {
            await output.WriteErrorLineAsync(policyError, cancellationToken);
            return new StepExecutionResult(1);
        }

        var filesystemError = DockerRuntimeSecurityPolicy.ValidateFilesystem(
            request.ProjectRoot,
            request.AdditionalMounts,
            $"Docker action '{request.StepName}'");
        if (filesystemError is not null)
        {
            await output.WriteErrorLineAsync(filesystemError, cancellationToken);
            return new StepExecutionResult(1);
        }

        var containerName = CreateContainerName(request.JobName, request.StepName);
        var observation = await ObserveImageUserAsync(
            request.Image,
            $"{surfaceKind}:{request.JobName}/{request.StepName}",
            cancellationToken);
        await WriteRootWarningAsync(observation, output, cancellationToken);
        using var process = new Process
        {
            StartInfo = CreateDockerActionStartInfo(request, containerName),
            EnableRaisingEvents = true
        };

        var result = await ExecuteDockerProcessAsync(process, containerName, output, cancellationToken);
        if (observation.Status == "unknown" && result.Success)
        {
            observation = await ObserveImageUserAsync(
                request.Image,
                $"{surfaceKind}:{request.JobName}/{request.StepName}",
                cancellationToken);
            await WriteRootWarningAsync(observation, output, cancellationToken);
        }

        return result;
    }

    public async Task<StepExecutionResult> ExecuteDockerfileActionAsync(
        DockerfileActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        var policyError = DockerRuntimeSecurityPolicy.Validate(
            [],
            request.AdditionalMounts,
            $"Dockerfile action '{request.StepName}'");
        if (policyError is not null)
        {
            await output.WriteErrorLineAsync(policyError, cancellationToken);
            return new StepExecutionResult(1);
        }

        var filesystemError = DockerRuntimeSecurityPolicy.ValidateFilesystem(
            request.ProjectRoot,
            request.AdditionalMounts,
            $"Dockerfile action '{request.StepName}'");
        if (filesystemError is not null)
        {
            await output.WriteErrorLineAsync(filesystemError, cancellationToken);
            return new StepExecutionResult(1);
        }

        var buildResult = await EnsureDockerfileActionImageAsync(request, output, cancellationToken);
        if (!buildResult.Success)
        {
            return buildResult;
        }

        return await ExecuteDockerActionCoreAsync(
            new DockerActionExecutionRequest(
                request.JobName,
                request.StepName,
                request.Image,
                request.ProjectRoot,
                request.Environment,
                request.AdditionalMounts,
                request.Runtime,
                request.EntryPoint,
                request.Arguments),
            output,
            "dockerfile-action",
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

        var buildContext = DockerfileBuildContextPreparer.Prepare(request);
        if (!buildContext.Success)
        {
            await output.WriteErrorLineAsync(buildContext.Error!, cancellationToken);
            return new StepExecutionResult(1);
        }

        using var process = new Process
        {
            StartInfo = CreateDockerfileActionBuildStartInfo(buildContext.Request!),
            EnableRaisingEvents = true
        };

        return await ExecuteDockerProcessAsync(process, null, output, cancellationToken);
    }

    public async Task<StepExecutionResult> ExecuteJavaScriptActionAsync(
        JavaScriptActionExecutionRequest request,
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        var policyError = DockerRuntimeSecurityPolicy.Validate(
            [],
            request.AdditionalMounts,
            $"JavaScript action '{request.StepName}'");
        if (policyError is not null)
        {
            await output.WriteErrorLineAsync(policyError, cancellationToken);
            return new StepExecutionResult(1);
        }

        var filesystemError = DockerRuntimeSecurityPolicy.ValidateFilesystem(
            request.ProjectRoot,
            request.AdditionalMounts,
            $"JavaScript action '{request.StepName}'");
        if (filesystemError is not null)
        {
            await output.WriteErrorLineAsync(filesystemError, cancellationToken);
            return new StepExecutionResult(1);
        }

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

    private async Task<StepExecutionResult> ExecuteJavaScriptActionPhaseAsync(
        JavaScriptActionExecutionRequest request,
        string scriptPath,
        string phase,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        var containerName = CreateContainerName(request.JobName, $"{request.StepName}-{phase}");
        var observation = await ObserveImageUserAsync(
            JavaScriptActionNodeImage,
            $"javascript-action:{request.JobName}/{request.StepName}/{phase}",
            cancellationToken);
        await WriteRootWarningAsync(observation, output, cancellationToken);
        using var process = new Process
        {
            StartInfo = CreateJavaScriptActionStartInfo(request, scriptPath, containerName),
            EnableRaisingEvents = true
        };

        var result = await ExecuteDockerProcessAsync(process, containerName, output, cancellationToken);
        if (observation.Status == "unknown" && result.Success)
        {
            observation = await ObserveImageUserAsync(
                JavaScriptActionNodeImage,
                $"javascript-action:{request.JobName}/{request.StepName}/{phase}",
                cancellationToken);
            await WriteRootWarningAsync(observation, output, cancellationToken);
        }

        return result;
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
            request.Runtime);
        AddShellInvocation(startInfo, image, request.Shell, request.Command);

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
            request.Runtime);
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
            request.Runtime);

        startInfo.ArgumentList.Add(JavaScriptActionNodeImage);
        startInfo.ArgumentList.Add("node");
        startInfo.ArgumentList.Add(ToActionContainerPath(request.ActionPath, scriptPath));

        return startInfo;
    }

    internal static ProcessStartInfo CreateServiceContainerStartInfo(
        JobRuntimeStartRequest request,
        ServiceContainerDefinition service,
        string networkName,
        string containerName)
    {
        DockerRuntimeSecurityPolicy.ThrowIfDenied(
            service.Options,
            service.Volumes,
            $"service '{service.Name}'");
        DockerRuntimeSecurityPolicy.ThrowIfFilesystemDenied(
            request.ProjectRoot,
            service.Volumes,
            $"service '{service.Name}'");
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
        DockerRuntimeSecurityPolicy.AddRuntimeArguments(startInfo);
        startInfo.ArgumentList.Add("--network");
        startInfo.ArgumentList.Add(networkName);
        startInfo.ArgumentList.Add("--network-alias");
        startInfo.ArgumentList.Add(service.Name);

        foreach (var port in service.Ports)
        {
            startInfo.ArgumentList.Add("--publish");
            startInfo.ArgumentList.Add(FormatPublishedPort(port));
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
        JobRuntimeContext? runtime)
    {
        var mounts = (container?.Volumes ?? []).Concat(additionalMounts ?? []);
        DockerRuntimeSecurityPolicy.ThrowIfDenied(
            container?.Options ?? [],
            mounts,
            $"job '{jobName}' step '{stepName}'");
        DockerRuntimeSecurityPolicy.ThrowIfFilesystemDenied(
            projectRoot,
            mounts,
            $"job '{jobName}' step '{stepName}'");
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
        DockerRuntimeSecurityPolicy.AddRuntimeArguments(startInfo);

        if (runtime is not null)
        {
            startInfo.ArgumentList.Add("--network");
            startInfo.ArgumentList.Add(runtime.NetworkName);
        }

        foreach (var port in container?.Ports ?? [])
        {
            startInfo.ArgumentList.Add("--publish");
            startInfo.ArgumentList.Add(FormatPublishedPort(port));
        }

        foreach (var option in container?.Options ?? [])
        {
            startInfo.ArgumentList.Add(option);
        }

        AddBindMount(startInfo, Path.GetFullPath(projectRoot), "/workspace", readOnly: false);
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
        AddBindMount(
            startInfo,
            FilesystemPathBoundary.ResolveExistingPath(mount.HostPath),
            ContainerFilesystemPolicy.NormalizeContainerPath(mount.ContainerPath),
            mount.ReadOnly);
    }

    private static void AddBindMount(
        ProcessStartInfo startInfo,
        string hostPath,
        string containerPath,
        bool readOnly)
    {
        startInfo.ArgumentList.Add("--mount");
        var specification = $"type=bind,src={hostPath},dst={containerPath}";
        startInfo.ArgumentList.Add(readOnly ? specification + ",readonly" : specification);
    }

    private static string NormalizeShell(string? shell)
        => string.IsNullOrWhiteSpace(shell) ? WorkflowShells.Sh : shell;

    private static void AddShellInvocation(
        ProcessStartInfo startInfo,
        string image,
        string? configuredShell,
        string command)
    {
        var shell = NormalizeShell(configuredShell);
        startInfo.ArgumentList.Add("--entrypoint");
        startInfo.ArgumentList.Add(shell);
        startInfo.ArgumentList.Add(image);

        if (string.Equals(shell, WorkflowShells.PowerShell, StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(BuildPowerShellScript(command));
            return;
        }

        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(BuildShellScript(command));
    }

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

    internal static string BuildPowerShellScript(string command)
    {
        return $$"""
            $ErrorActionPreference = 'Stop'
            $PSNativeCommandUseErrorActionPreference = $true
            {{command}}
            if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
            """;
    }

    internal static ProcessStartInfo CreateNetworkCreateStartInfo(string jobName, string networkName)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("network");
        startInfo.ArgumentList.Add("create");
        startInfo.ArgumentList.Add("--driver");
        startInfo.ArgumentList.Add("bridge");
        startInfo.ArgumentList.Add("--opt");
        startInfo.ArgumentList.Add("com.docker.network.bridge.host_binding_ipv4=127.0.0.1");
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

    internal static string FormatPublishedPort(ContainerPortMapping port)
    {
        var hostPort = port.HostPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return $"127.0.0.1:{hostPort}:{port.ContainerPort}/{port.Protocol}";
    }

    internal static RunnerNetworkObservation CreateNetworkObservation(
        JobRuntimeStartRequest request,
        string networkName)
    {
        var ports = request.JobContainerPorts
            .Select(port => ToPublishedPort("job-container", port))
            .Concat(request.Services.SelectMany(service =>
                service.Ports.Select(port => ToPublishedPort($"service:{service.Name}", port))))
            .ToArray();

        return new RunnerNetworkObservation(
            request.JobName,
            networkName,
            "user-defined-bridge",
            OutboundAllowed: true,
            Internal: false,
            request.Services.Select(service => service.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            ports);
    }

    private static RunnerPublishedPort ToPublishedPort(string surface, ContainerPortMapping port)
        => new(surface, "127.0.0.1", port.ContainerPort, port.HostPort, port.Protocol);

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

    private static ProcessStartInfo CreateImageUserInspectStartInfo(string image)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("image");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{json .Config.User}}");
        startInfo.ArgumentList.Add(image);
        return startInfo;
    }

    private async Task<RunnerImageUserObservation> ObserveImageUserAsync(
        string image,
        string surface,
        CancellationToken cancellationToken)
    {
        string configuredUser;
        string status;
        if (_imageUsers.TryGetValue(image, out var cached))
        {
            (configuredUser, status) = cached;
        }
        else
        {
            var result = await RunDockerCommandAsync(CreateImageUserInspectStartInfo(image), cancellationToken);
            configuredUser = result.Success
                ? result.StandardOutput.Trim().Trim('"')
                : string.Empty;
            status = result.Success
                ? IsRootConfiguredUser(configuredUser) ? "root" : "non-root"
                : "unknown";
            if (status != "unknown")
            {
                _imageUsers[image] = (configuredUser, status);
            }
        }
        var observation = new RunnerImageUserObservation(
            surface,
            image,
            string.IsNullOrWhiteSpace(configuredUser) ? "<image-default-root>" : configuredUser,
            status);
        _imageUserObservations[$"{surface}|{image}"] = observation;
        return observation;
    }

    private static bool IsRootConfiguredUser(string configuredUser)
    {
        if (string.IsNullOrWhiteSpace(configuredUser))
        {
            return true;
        }

        var user = configuredUser.Split(':', 2)[0];
        return user.Equals("0", StringComparison.Ordinal) ||
            user.Equals("root", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteRootWarningAsync(
        RunnerImageUserObservation observation,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        if (observation.Status == "root" && _warnedRootImages.TryAdd(observation.Image, 0))
        {
            await output.WriteErrorLineAsync($"warning: {CreateRootUserWarning(observation)}", cancellationToken);
        }
    }

    private static string CreateRootUserWarning(RunnerImageUserObservation observation)
        => $"secure-baseline image '{observation.Image}' uses root as its configured user for {observation.Surface}; use an image with a non-root USER when compatible.";

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
