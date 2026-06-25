namespace Actio.Web.Models;

public sealed record WorkflowFileUpdateResult(
    bool Success,
    IReadOnlyList<string> Errors)
{
    public static WorkflowFileUpdateResult Saved()
        => new(true, []);

    public static WorkflowFileUpdateResult Failed(IReadOnlyList<string> errors)
        => new(false, errors);
}
