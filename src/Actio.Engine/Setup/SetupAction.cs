namespace Actio.Engine.Setup;

internal sealed record SetupAction(
    SetupActionKind Kind,
    string ActionName,
    string? RequestedVersion,
    string? VersionMatchPattern,
    string? Distribution);
