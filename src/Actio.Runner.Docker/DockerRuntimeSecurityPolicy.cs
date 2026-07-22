using System.Diagnostics;
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

    internal static void AddRuntimeArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("--security-opt");
        startInfo.ArgumentList.Add(NoNewPrivileges);
    }

    internal static string? Validate(
        IEnumerable<string> options,
        IEnumerable<StepExecutionMount> mounts,
        string surface)
    {
        foreach (var option in options)
        {
            var optionName = option.Split('=', 2)[0];
            if (ContainerSecurityOptionPolicy.IsDenied(optionName))
            {
                return $"{ProfileName} blocked Docker option '{optionName}' for {surface}. " +
                    "Actio does not permit privilege, host namespace, device, confinement override, or arbitrary mount controls; use an unprivileged image and Actio's declared workspace or service features.";
            }
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

    internal static void ThrowIfDenied(
        IEnumerable<string> options,
        IEnumerable<StepExecutionMount> mounts,
        string surface)
    {
        var error = Validate(options, mounts, surface);
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
}
