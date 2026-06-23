namespace Actio.Engine.Runs;

public sealed record WorkflowRunOutput(
    string JobName,
    string Name,
    string Value);
