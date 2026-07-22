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
        "--security-opt",
        "--mount",
        "--volume",
        "-v",
        "--volumes-from",
        "--volume-driver",
        "--use-api-socket",
        "--runtime",
        "--gpus"
    };

    public static bool IsDenied(string option)
        => DeniedOptions.Contains(option.Split('=', 2)[0]);
}
