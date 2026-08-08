namespace Actio.Core.Actions;

public sealed record ActionReference(
    string Value,
    ActionReferenceKind Kind,
    string Target,
    bool IsPinned,
    string? MutablePart,
    string? RequestedRef = null)
{
    private const string DockerPrefix = "docker://";
    private const string Sha256Prefix = "@sha256:";

    public bool IsRemote => Kind is ActionReferenceKind.DockerImage or ActionReferenceKind.GitHubRepository;

    public bool IsMutable => IsRemote && !IsPinned;

    public static bool IsSupportedLocalReference(string value)
    {
        return value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith(".\\", StringComparison.Ordinal);
    }

    public static bool TryParse(string value, out ActionReference? reference)
    {
        reference = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IsSupportedLocalReference(value))
        {
            reference = new ActionReference(value, ActionReferenceKind.Local, value, IsPinned: true, MutablePart: null);
            return true;
        }

        if (value.StartsWith(DockerPrefix, StringComparison.Ordinal))
        {
            return TryParseDockerReference(value, out reference);
        }

        return TryParseGitHubReference(value, out reference);
    }

    private static bool TryParseDockerReference(string value, out ActionReference? reference)
    {
        reference = null;
        var image = value[DockerPrefix.Length..];
        if (string.IsNullOrWhiteSpace(image) || image.StartsWith('-') || ContainsWhitespace(image))
        {
            return false;
        }

        if (image.Contains(Sha256Prefix, StringComparison.OrdinalIgnoreCase) && !IsDockerDigestPinned(image))
        {
            return false;
        }

        var pinned = IsDockerDigestPinned(image);
        reference = new ActionReference(
            value,
            ActionReferenceKind.DockerImage,
            image,
            pinned,
            pinned ? null : GetDockerMutablePart(image));
        return true;
    }

    private static bool TryParseGitHubReference(string value, out ActionReference? reference)
    {
        reference = null;
        var refSeparator = value.LastIndexOf('@');
        if (refSeparator <= 0 || refSeparator == value.Length - 1)
        {
            return false;
        }

        var path = value[..refSeparator];
        var requestedRef = value[(refSeparator + 1)..];
        var parts = path.Split('/');
        if (parts.Length < 2 ||
            parts.Any(part => !IsGitHubPathPart(part)) ||
            !IsGitHubRef(requestedRef))
        {
            return false;
        }

        var pinned = IsFullCommitSha(requestedRef);
        reference = new ActionReference(
            value,
            ActionReferenceKind.GitHubRepository,
            path,
            pinned,
            pinned ? null : requestedRef,
            requestedRef);
        return true;
    }

    public bool TryGetGitHubAction(out GitHubActionReference? githubAction)
    {
        githubAction = null;

        if (Kind is not ActionReferenceKind.GitHubRepository || RequestedRef is null)
        {
            return false;
        }

        var parts = Target.Split('/');
        if (parts.Length < 2)
        {
            return false;
        }

        githubAction = new GitHubActionReference(
            parts[0],
            parts[1],
            string.Join('/', parts.Skip(2)),
            RequestedRef);
        return true;
    }

    private static bool IsDockerDigestPinned(string image)
    {
        var digestIndex = image.LastIndexOf(Sha256Prefix, StringComparison.OrdinalIgnoreCase);
        if (digestIndex < 0)
        {
            return false;
        }

        var digest = image[(digestIndex + Sha256Prefix.Length)..];
        return digest.Length == 64 && digest.All(IsHex);
    }

    private static string GetDockerMutablePart(string image)
    {
        var tagIndex = image.LastIndexOf(':');
        var slashIndex = image.LastIndexOf('/');
        return tagIndex > slashIndex && tagIndex < image.Length - 1
            ? image[(tagIndex + 1)..]
            : "latest";
    }

    private static bool IsGitHubPathPart(string part)
    {
        return !string.IsNullOrWhiteSpace(part) &&
            part is not "." and not ".." &&
            part.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool IsGitHubRef(string requestedRef)
    {
        return !string.IsNullOrWhiteSpace(requestedRef) &&
            requestedRef.All(character => !char.IsWhiteSpace(character) && character != '\\');
    }

    private static bool IsFullCommitSha(string requestedRef)
    {
        return requestedRef.Length == 40 && requestedRef.All(IsHex);
    }

    private static bool IsHex(char character)
    {
        return character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static bool ContainsWhitespace(string value)
    {
        return value.Any(char.IsWhiteSpace);
    }
}
