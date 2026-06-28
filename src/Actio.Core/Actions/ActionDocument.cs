namespace Actio.Core.Actions;

public sealed record ActionDocument(
    string Name,
    IReadOnlyList<ActionStep> Steps,
    IReadOnlyDictionary<string, ActionInput>? Inputs = null)
{
    public IReadOnlyDictionary<string, ActionInput> Inputs { get; init; } =
        Inputs ?? new Dictionary<string, ActionInput>();
}

public sealed record ActionInput(
    string Name,
    string? Description,
    bool Required,
    string? Default);

public sealed record ActionStep(
    string Name,
    string Run);
