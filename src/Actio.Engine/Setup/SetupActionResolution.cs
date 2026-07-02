namespace Actio.Engine.Setup;

internal sealed record SetupActionResolution(
    bool Success,
    SetupAction? Action,
    IReadOnlyList<string> Errors)
{
    public static SetupActionResolution NotSetupAction { get; } = new(true, null, []);

    public static SetupActionResolution Resolved(SetupAction action)
        => new(true, action, []);

    public static SetupActionResolution Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}
