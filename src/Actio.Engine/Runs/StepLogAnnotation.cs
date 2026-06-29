namespace Actio.Engine.Runs;

public sealed record StepLogAnnotation(
    string Level,
    string Message,
    string? Title = null,
    string? File = null,
    int? Line = null,
    int? EndLine = null,
    int? Column = null,
    int? EndColumn = null);
