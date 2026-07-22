namespace Actio.Engine.Runs;

public sealed record RunnerImageUserObservation(
    string Surface,
    string Image,
    string ConfiguredUser,
    string Status);
