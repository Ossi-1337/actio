namespace Actio.Engine.Execution;

public static class RunnerSecurityProfiles
{
    public const string SecureBaseline = "secure-baseline";
    public const string Strict = "strict";

    public static bool IsSupported(string value)
        => string.Equals(value, SecureBaseline, StringComparison.Ordinal) ||
            string.Equals(value, Strict, StringComparison.Ordinal);
}

public sealed record ContainerResourceConfiguration(
    double? Cpu = null,
    long? MemoryMiB = null,
    int? Pids = null,
    long? TempMiB = null,
    long? DockerLogMiB = null,
    int? DockerLogFiles = null,
    long? StepLogMiB = null);

public sealed record ContainerResourceLimits(
    double Cpu,
    long MemoryBytes,
    int Pids,
    long TempBytes,
    long DockerLogBytes,
    int DockerLogFiles,
    long StepLogBytes)
{
    public static ContainerResourceLimits Defaults { get; } = new(
        2,
        4096L * 1024 * 1024,
        512,
        512L * 1024 * 1024,
        10L * 1024 * 1024,
        3,
        50L * 1024 * 1024);
}

public sealed record ActioInstanceIdentity(
    string InstanceId,
    int OwnerProcessId,
    long OwnerProcessStartUtcTicks);

public sealed record RunnerExecutionPolicy(
    string RequestedProfile,
    ContainerResourceConfiguration ResourceConfiguration,
    ActioInstanceIdentity InstanceIdentity)
{
    public static RunnerExecutionPolicy CreateDefault()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(
            RunnerSecurityProfiles.SecureBaseline,
            new ContainerResourceConfiguration(),
            new ActioInstanceIdentity(
                "ephemeral",
                Environment.ProcessId,
                process.StartTime.ToUniversalTime().Ticks));
    }
}

public sealed record RunnerPreflightRequest(
    string RunId,
    RunnerExecutionPolicy Policy,
    bool HasPublishedPorts);

public sealed record RunnerExecutionContext(
    string RunId,
    string RequestedProfile,
    string EffectiveProfile,
    ContainerResourceLimits ResourceLimits,
    ActioInstanceIdentity InstanceIdentity);

public sealed record RunnerPreflightResult(
    bool Success,
    RunnerExecutionContext? Context,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> Errors { get; init; } = Errors ?? [];

    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? [];

    public static RunnerPreflightResult Passed(
        RunnerExecutionContext context,
        IReadOnlyList<string>? warnings = null)
        => new(true, context, [], warnings);

    public static RunnerPreflightResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}
