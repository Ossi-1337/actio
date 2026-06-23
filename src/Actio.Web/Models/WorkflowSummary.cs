namespace Actio.Web.Models;

public sealed record WorkflowSummary(
    string Name,
    string FileName,
    string Path,
    int RunCount,
    string? LatestRunId,
    string? LatestStatus,
    DateTimeOffset? LatestStartedAt);
