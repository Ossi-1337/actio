using Actio.Engine.Execution;

namespace Actio.Engine.Runs;

public sealed record RunnerSecurityMetadata(
    string Provider,
    string RequestedProfile,
    string EffectiveProfile,
    IReadOnlyList<string>? AppliedSecurityOptions = null,
    string CapabilityPolicy = "not-reported",
    string ConfinementPolicy = "not-reported",
    string DaemonPlatformState = "not-evaluated",
    IReadOnlyList<string>? DegradedControls = null,
    string UserPolicy = "not-reported",
    string RootFilesystemPolicy = "not-reported",
    string WorkspacePolicy = "not-reported",
    string MountPolicy = "not-reported",
    IReadOnlyList<string>? ProtectedPaths = null,
    IReadOnlyList<RunnerImageUserObservation>? ImageUserObservations = null,
    string NetworkPolicy = "not-reported",
    string PublishedPortPolicy = "not-reported",
    IReadOnlyList<RunnerNetworkObservation>? NetworkObservations = null,
    ContainerResourceLimits? RequestedResourceLimits = null,
    ContainerResourceLimits? EffectiveResourceLimits = null,
    RunnerPreflightEvidence? Preflight = null,
    RunnerCleanupEvidence? Cleanup = null,
    IReadOnlyList<string>? StrictControls = null,
    IReadOnlyList<RunnerJavaScriptRuntimeObservation>? JavaScriptRuntimeObservations = null)
{
    public IReadOnlyList<string> AppliedSecurityOptions { get; init; } = AppliedSecurityOptions ?? [];

    public IReadOnlyList<string> DegradedControls { get; init; } = DegradedControls ?? [];

    public IReadOnlyList<string> ProtectedPaths { get; init; } = ProtectedPaths ?? [];

    public IReadOnlyList<RunnerImageUserObservation> ImageUserObservations { get; init; } = ImageUserObservations ?? [];

    public IReadOnlyList<RunnerNetworkObservation> NetworkObservations { get; init; } = NetworkObservations ?? [];

    public IReadOnlyList<string> StrictControls { get; init; } = StrictControls ?? [];

    public IReadOnlyList<RunnerJavaScriptRuntimeObservation> JavaScriptRuntimeObservations { get; init; } =
        JavaScriptRuntimeObservations ?? [];
}

public sealed record RunnerPreflightEvidence(
    string Status = "not-reported",
    string EngineVersion = "not-reported",
    string OperatingSystem = "not-reported",
    string CgroupVersion = "not-reported",
    string CgroupDriver = "not-reported",
    string Seccomp = "not-reported",
    string Rootless = "not-reported",
    string UserNamespace = "not-reported",
    string DockerDesktop = "not-reported",
    string EnhancedContainerIsolation = "not-reported",
    string LoggingDriver = "not-reported",
    string DirectRouting = "not-evaluated",
    double CpuCapacity = 0,
    long MemoryCapacityBytes = 0,
    bool CpuLimitSupported = false,
    bool MemoryLimitSupported = false,
    bool SwapLimitSupported = false,
    bool PidsLimitSupported = false);

public sealed record RunnerCleanupEvidence(
    int CandidateContainers = 0,
    int RemovedContainers = 0,
    int CandidateNetworks = 0,
    int RemovedNetworks = 0,
    int SkippedActive = 0,
    int SkippedUnverifiable = 0,
    IReadOnlyList<string>? Errors = null)
{
    public IReadOnlyList<string> Errors { get; init; } = Errors ?? [];
}
