namespace Actio.Web.Models;

public sealed record WorkflowFileResult(
    string FileName,
    string Path,
    string Content);
