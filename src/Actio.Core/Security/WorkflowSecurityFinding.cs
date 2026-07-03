namespace Actio.Core.Security;

public sealed record WorkflowSecurityFinding(
    string Severity,
    string Category,
    string Location,
    string Message,
    string Recommendation,
    string? Reference = null,
    string? ReferenceKind = null,
    bool? IsPinned = null,
    string? MutablePart = null);
