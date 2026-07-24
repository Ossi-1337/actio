using System.Diagnostics;
using System.Globalization;
using Actio.Core.Workflows;
using Actio.Engine.Execution;
using Actio.Engine.Runs;

namespace Actio.Runner.Docker;

internal static class DockerRuntimeSecurityPolicy
{
    internal const string ProfileName = ContainerSecurityOptionPolicy.ProfileName;
    internal const string NoNewPrivileges = "no-new-privileges=true";

    internal static RunnerSecurityMetadata Metadata { get; } = new(
        "docker",
        ProfileName,
        ProfileName,
        [NoNewPrivileges],
        "docker-default-no-additions",
        "docker-default-seccomp-and-lsm-preserved",
        "not-evaluated",
        ["daemon-platform-security-not-evaluated"],
        "image-default-user-with-root-warning",
        "writable",
        "read-write-with-protected-value-file-masks",
        "canonical-existing-bind-sources-only",
        ["/workspace/.actio/secrets.env", "/workspace/.actio/vars.env"],
        NetworkPolicy: "per-job-user-defined-bridge-with-outbound",
        PublishedPortPolicy: "ipv4-loopback-only");

    internal static void AddRuntimeArguments(
        ProcessStartInfo startInfo,
        RunnerExecutionContext? execution = null,
        IReadOnlyList<string>? workflowOptions = null)
    {
        startInfo.ArgumentList.Add("--security-opt");
        startInfo.ArgumentList.Add(NoNewPrivileges);

        var limits = ApplyResourceReductions(
            execution?.ResourceLimits ?? ContainerResourceLimits.Defaults,
            workflowOptions ?? []);
        startInfo.ArgumentList.Add("--cpus");
        startInfo.ArgumentList.Add(limits.Cpu.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--memory");
        startInfo.ArgumentList.Add(limits.MemoryBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--memory-swap");
        startInfo.ArgumentList.Add(limits.MemoryBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--pids-limit");
        startInfo.ArgumentList.Add(limits.Pids.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--tmpfs");
        startInfo.ArgumentList.Add($"/tmp:rw,nosuid,nodev,noexec,size={limits.TempBytes}");
        startInfo.ArgumentList.Add("--log-opt");
        startInfo.ArgumentList.Add($"max-size={limits.DockerLogBytes}");
        startInfo.ArgumentList.Add("--log-opt");
        startInfo.ArgumentList.Add($"max-file={limits.DockerLogFiles}");
        startInfo.ArgumentList.Add("--log-driver");
        startInfo.ArgumentList.Add("local");
        startInfo.ArgumentList.Add("--ulimit");
        startInfo.ArgumentList.Add("core=0");
        startInfo.ArgumentList.Add("--ulimit");
        startInfo.ArgumentList.Add("nofile=65536:65536");

        if (string.Equals(execution?.EffectiveProfile, RunnerSecurityProfiles.Strict, StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--cap-drop");
            startInfo.ArgumentList.Add("ALL");
            startInfo.ArgumentList.Add("--read-only");
            startInfo.ArgumentList.Add("--tmpfs");
            startInfo.ArgumentList.Add($"/run:rw,nosuid,nodev,noexec,size={limits.TempBytes}");
        }
    }

    internal static void AddOwnershipLabels(
        ProcessStartInfo startInfo,
        RunnerExecutionContext? execution,
        string jobName,
        string resourceType)
    {
        if (execution is null)
        {
            return;
        }

        AddLabel(startInfo, "io.actio.managed", "true");
        AddLabel(startInfo, "io.actio.instance", execution.InstanceIdentity.InstanceId);
        AddLabel(startInfo, "io.actio.run", execution.RunId);
        AddLabel(startInfo, "io.actio.job", jobName);
        AddLabel(startInfo, "io.actio.resource", resourceType);
        AddLabel(startInfo, "io.actio.owner-pid", execution.InstanceIdentity.OwnerProcessId.ToString(CultureInfo.InvariantCulture));
        AddLabel(startInfo, "io.actio.owner-start", execution.InstanceIdentity.OwnerProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddLabel(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"{name}={value}");
    }

    internal static string? Validate(
        IEnumerable<string> options,
        IEnumerable<StepExecutionMount> mounts,
        string surface,
        RunnerExecutionContext? execution = null)
    {
        var optionList = options.ToArray();
        foreach (var option in optionList)
        {
            var optionName = option.Split('=', 2)[0];
            if (ContainerSecurityOptionPolicy.IsDenied(optionName))
            {
                return $"{ProfileName} blocked Docker option '{optionName}' for {surface}. " +
                    "Actio does not permit privilege, host namespace, device, confinement override, or arbitrary mount controls; use an unprivileged image and Actio's declared workspace or service features.";
            }
        }

        var resourceError = ValidateResourceOptions(optionList, execution, surface);
        if (resourceError is not null)
        {
            return resourceError;
        }

        foreach (var mount in mounts)
        {
            if (IsContainerRuntimeSocket(mount.HostPath))
            {
                return $"{ProfileName} blocked container runtime socket mount '{mount.HostPath}' for {surface}. " +
                    "Docker, Podman, and containerd API sockets cannot be exposed to workflow containers.";
            }
        }

        return null;
    }

    internal static string? ValidateFilesystem(
        string projectRoot,
        IEnumerable<StepExecutionMount> mounts,
        string surface)
    {
        var errors = ContainerFilesystemPolicy.ValidateMounts(projectRoot, mounts);
        if (errors.Count > 0)
        {
            return $"{surface}: {string.Join(" ", errors)}";
        }

        return null;
    }

    internal static string? ValidatePublishedPorts(
        IEnumerable<ContainerPortMapping> ports,
        RunnerExecutionContext? execution)
    {
        if (string.Equals(
                execution?.EffectiveProfile,
                RunnerSecurityProfiles.Strict,
                StringComparison.Ordinal) &&
            ports.Any())
        {
            return "Strict profile blocks host port publication. Remove container or service ports.";
        }

        return null;
    }

    internal static void ThrowIfDenied(
        IEnumerable<string> options,
        IEnumerable<StepExecutionMount> mounts,
        string surface,
        RunnerExecutionContext? execution = null)
    {
        var error = Validate(options, mounts, surface, execution);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    internal static void ThrowIfFilesystemDenied(
        string projectRoot,
        IEnumerable<StepExecutionMount> mounts,
        string surface)
    {
        var error = ValidateFilesystem(projectRoot, mounts, surface);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static bool IsContainerRuntimeSocket(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return fileName.Equals("docker.sock", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("podman.sock", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("containerd.sock", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("//./pipe/docker_engine", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateResourceOptions(
        IReadOnlyList<string> options,
        RunnerExecutionContext? execution,
        string surface)
    {
        var limits = execution?.ResourceLimits ?? ContainerResourceLimits.Defaults;
        for (var index = 0; index < options.Count; index++)
        {
            var token = options[index];
            var parts = token.Split('=', 2);
            var name = parts[0];
            if (name is not ("--cpus" or "--memory" or "--memory-reservation" or "--ulimit"))
            {
                continue;
            }

            var value = parts.Length == 2
                ? parts[1]
                : index + 1 < options.Count ? options[++index] : string.Empty;

            if (name == "--cpus" &&
                (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu) ||
                 cpu <= 0 ||
                 cpu > limits.Cpu))
            {
                return $"{ProfileName} requires {surface} --cpus to be positive and no greater than {limits.Cpu}.";
            }

            if (name is "--memory" or "--memory-reservation")
            {
                if (!TryParseMemory(value, out var bytes) || bytes <= 0 || bytes > limits.MemoryBytes)
                {
                    return $"{ProfileName} requires {surface} {name} to be positive and no greater than {limits.MemoryBytes} bytes.";
                }
            }

            if (name == "--ulimit" && !IsSupportedUlimit(value))
            {
                return $"{ProfileName} permits {surface} --ulimit only as core=0 or nofile with soft/hard values no greater than 65536.";
            }
        }

        return null;
    }

    private static bool IsSupportedUlimit(string value)
    {
        if (string.Equals(value, "core=0", StringComparison.Ordinal))
        {
            return true;
        }

        if (!value.StartsWith("nofile=", StringComparison.Ordinal))
        {
            return false;
        }

        var limits = value["nofile=".Length..].Split(':', 2);
        return int.TryParse(limits[0], out var soft) &&
            soft > 0 &&
            soft <= 65536 &&
            (limits.Length == 1 ||
             int.TryParse(limits[1], out var hard) && hard >= soft && hard <= 65536);
    }

    private static bool TryParseMemory(string value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var multiplier = 1L;
        var number = value;
        var suffix = char.ToLowerInvariant(value[^1]);
        if (suffix is 'k' or 'm' or 'g')
        {
            number = value[..^1];
            multiplier = suffix switch
            {
                'k' => 1024L,
                'm' => 1024L * 1024,
                'g' => 1024L * 1024 * 1024,
                _ => 1L
            };
        }

        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            TryMultiply(parsed, multiplier, out bytes);
    }

    private static ContainerResourceLimits ApplyResourceReductions(
        ContainerResourceLimits limits,
        IReadOnlyList<string> options)
    {
        var cpu = limits.Cpu;
        var memory = limits.MemoryBytes;
        for (var index = 0; index < options.Count; index++)
        {
            var parts = options[index].Split('=', 2);
            var name = parts[0];
            if (name is not ("--cpus" or "--memory"))
            {
                continue;
            }

            var value = parts.Length == 2
                ? parts[1]
                : index + 1 < options.Count ? options[++index] : string.Empty;
            if (name == "--cpus" &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCpu))
            {
                cpu = parsedCpu;
            }
            else if (name == "--memory" && TryParseMemory(value, out var parsedMemory))
            {
                memory = parsedMemory;
            }
        }

        return limits with { Cpu = cpu, MemoryBytes = memory };
    }

    private static bool TryMultiply(long value, long multiplier, out long result)
    {
        try
        {
            result = checked(value * multiplier);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
