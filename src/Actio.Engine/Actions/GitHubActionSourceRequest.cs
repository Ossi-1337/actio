namespace Actio.Engine.Actions;

public sealed record GitHubActionSourceRequest(
    string Uses,
    string Owner,
    string Repository,
    string ActionPath,
    string Ref,
    bool IsPinned,
    string? MutablePart);
