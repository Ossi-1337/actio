namespace Actio.Engine.Runs;

public sealed record StepRunRecord(
    string Name,
    string Status,
    string Command,
    int? ExitCode,
    string? LogPath,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMilliseconds,
    string? Id = null,
    string? Shell = null,
    string? WorkingDirectory = null,
    string? If = null,
    int? TimeoutMinutes = null,
    bool ContinueOnError = false,
    string? SummaryPath = null,
    string? Summary = null,
    IReadOnlyList<StepLogAnnotation>? Annotations = null)
{
    public IReadOnlyList<StepLogAnnotation> Annotations { get; init; } = Annotations ?? [];
}
