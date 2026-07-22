namespace Actio.Engine.Runs;

public sealed record RunnerSecurityMetadata(
    string Provider,
    string RequestedProfile,
    string EffectiveProfile,
    IReadOnlyList<string>? AppliedSecurityOptions = null,
    string CapabilityPolicy = "not-reported",
    string ConfinementPolicy = "not-reported",
    string DaemonPlatformState = "not-evaluated",
    IReadOnlyList<string>? DegradedControls = null)
{
    public IReadOnlyList<string> AppliedSecurityOptions { get; init; } = AppliedSecurityOptions ?? [];

    public IReadOnlyList<string> DegradedControls { get; init; } = DegradedControls ?? [];
}
