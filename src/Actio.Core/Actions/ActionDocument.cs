namespace Actio.Core.Actions;

public sealed record ActionDocument(
    string Name,
    IReadOnlyList<ActionStep> Steps);

public sealed record ActionStep(
    string Name,
    string Run);
