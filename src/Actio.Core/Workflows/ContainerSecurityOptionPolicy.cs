namespace Actio.Core.Workflows;

public static class ContainerSecurityOptionPolicy
{
    public const string ProfileName = "secure-baseline";

    private static readonly HashSet<string> DeniedOptions = new(StringComparer.Ordinal)
    {
        "--privileged",
        "--cap-add",
        "--device",
        "--device-cgroup-rule",
        "--device-read-bps",
        "--device-read-iops",
        "--device-write-bps",
        "--device-write-iops",
        "--blkio-weight-device",
        "--pid",
        "--ipc",
        "--uts",
        "--cgroupns",
        "--userns",
        "--network",
        "--net",
        "--network-alias",
        "--link",
        "--link-local-ip",
        "--ip",
        "--ip6",
        "--mac-address",
        "--publish",
        "-p",
        "--publish-all",
        "-P",
        "--expose",
        "--security-opt",
        "--mount",
        "--volume",
        "-v",
        "--volumes-from",
        "--volume-driver",
        "--use-api-socket",
        "--runtime",
        "--gpus",
        "--cpu-period",
        "--cpu-quota",
        "--cpu-rt-period",
        "--cpu-rt-runtime",
        "--cpu-shares",
        "--cpuset-cpus",
        "--cpuset-mems",
        "--pids-limit",
        "--tmpfs",
        "--log-driver",
        "--log-opt",
        "--memory-swap",
        "--memory-swappiness",
        "--oom-kill-disable",
        "--oom-score-adj",
        "--restart"
    };

    public static bool IsDenied(string option)
        => DeniedOptions.Contains(option.Split('=', 2)[0]);
}
