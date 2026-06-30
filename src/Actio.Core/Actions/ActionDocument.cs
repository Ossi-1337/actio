namespace Actio.Core.Actions;

public sealed record ActionDocument(
    string Name,
    IReadOnlyList<ActionStep> Steps,
    IReadOnlyDictionary<string, ActionInput>? Inputs = null,
    string Runtime = ActionRuntime.Composite,
    string? Image = null,
    string? Main = null,
    string? Pre = null,
    string? Post = null)
{
    public IReadOnlyDictionary<string, ActionInput> Inputs { get; init; } =
        Inputs ?? new Dictionary<string, ActionInput>();
}

public static class ActionRuntime
{
    public const string Composite = "composite";
    public const string Docker = "docker";
    public const string Node20 = "node20";
}

public sealed record ActionInput(
    string Name,
    string? Description,
    bool Required,
    string? Default);

public sealed record ActionStep(
    string Name,
    string Run);
