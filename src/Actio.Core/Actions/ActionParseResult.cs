namespace Actio.Core.Actions;

public sealed record ActionParseResult(
    bool Success,
    ActionDocument? Action,
    IReadOnlyList<string> Errors)
{
    public static ActionParseResult Parsed(ActionDocument action)
        => new(true, action, []);

    public static ActionParseResult Failed(IReadOnlyList<string> errors)
        => new(false, null, errors);
}
