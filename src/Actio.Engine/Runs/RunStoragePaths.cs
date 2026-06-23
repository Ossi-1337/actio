namespace Actio.Engine.Runs;

public sealed record RunStoragePaths(
    string RunId,
    string? RunDirectory,
    string? RunRecordPath);
