namespace Actio.Engine.Runs;

public sealed record RunStoragePaths(
    string RunId,
    string? RunDirectory,
    string? RunRecordPath,
    string? ActioHomePath = null,
    IReadOnlyDictionary<string, string>? WorkspaceMaskFiles = null)
{
    public IReadOnlyDictionary<string, string> WorkspaceMaskFiles { get; init; } =
        WorkspaceMaskFiles ?? new Dictionary<string, string>();
}
