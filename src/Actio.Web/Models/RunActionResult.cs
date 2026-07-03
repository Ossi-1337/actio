namespace Actio.Web.Models;

public sealed record RunActionResult(
    bool Success,
    string? RunId,
    IReadOnlyList<string> Errors)
{
    public static RunActionResult Accepted(string runId)
        => new(true, runId, []);

    public static RunActionResult Completed()
        => new(true, null, []);

    public static RunActionResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}
