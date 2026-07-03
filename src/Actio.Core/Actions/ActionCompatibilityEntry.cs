namespace Actio.Core.Actions;

public sealed record ActionCompatibilityEntry(
    string Name,
    string Owner,
    string Repository,
    string ActionPath,
    ActionCompatibilityStatus Status,
    string ActionType,
    string SupportedRefs,
    string CurrentBehavior,
    string Limitations,
    string RequiredMilestone,
    string Evidence)
{
    public bool Matches(GitHubActionReference action)
    {
        return string.Equals(action.Owner, Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Repository, Repository, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.ActionPath, ActionPath, StringComparison.Ordinal);
    }

    public string FormatUnsupportedMessage(string uses)
    {
        return $"Action '{uses}' is listed in Actio's compatibility matrix as {StatusText}. {Limitations} Required milestone: {RequiredMilestone}.";
    }

    public string StatusText => Status switch
    {
        ActionCompatibilityStatus.Supported => "Supported",
        ActionCompatibilityStatus.Partial => "Partial",
        ActionCompatibilityStatus.Unsupported => "Unsupported",
        ActionCompatibilityStatus.Unvalidated => "Unvalidated",
        _ => Status.ToString()
    };
}
