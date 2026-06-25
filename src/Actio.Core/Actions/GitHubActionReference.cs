namespace Actio.Core.Actions;

public sealed record GitHubActionReference(
    string Owner,
    string Repository,
    string ActionPath,
    string Ref);
