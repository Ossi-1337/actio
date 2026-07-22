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
    IReadOnlyList<RunnerImageUserObservation>? ImageUserObservations = null)
{
    public IReadOnlyList<string> AppliedSecurityOptions { get; init; } = AppliedSecurityOptions ?? [];

    public IReadOnlyList<string> DegradedControls { get; init; } = DegradedControls ?? [];

    public IReadOnlyList<string> ProtectedPaths { get; init; } = ProtectedPaths ?? [];

    public IReadOnlyList<RunnerImageUserObservation> ImageUserObservations { get; init; } = ImageUserObservations ?? [];
}
