namespace Actio.Git;

public sealed record GitPushRefUpdate(
    string LocalRef,
    string LocalObjectId,
    string RemoteRef,
    string RemoteObjectId)
{
    public bool IsDeletion => IsZeroObjectId(LocalObjectId);

    public bool IsNewRef => IsZeroObjectId(RemoteObjectId);

    public GitReferenceKind ReferenceKind => RemoteRef switch
    {
        var value when value.StartsWith("refs/heads/", StringComparison.Ordinal) => GitReferenceKind.Branch,
        var value when value.StartsWith("refs/tags/", StringComparison.Ordinal) => GitReferenceKind.Tag,
        _ => GitReferenceKind.Unsupported
    };

    public string ReferenceName => ReferenceKind switch
    {
        GitReferenceKind.Branch => RemoteRef["refs/heads/".Length..],
        GitReferenceKind.Tag => RemoteRef["refs/tags/".Length..],
        _ => RemoteRef
    };

    private static bool IsZeroObjectId(string value)
        => value.All(character => character == '0');
}

public enum GitReferenceKind
{
    Unsupported,
    Branch,
    Tag
}

public sealed record GitPrePushParseResult(
    bool Success,
    IReadOnlyList<GitPushRefUpdate> Updates,
    IReadOnlyList<string> Errors);

public static class GitPrePushInputParser
{
    public static GitPrePushParseResult Parse(string input)
    {
        var updates = new List<GitPushRefUpdate>();
        var errors = new List<string>();
        var lineNumber = 0;

        using var reader = new StringReader(input);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4)
            {
                errors.Add($"Pre-push input line {lineNumber} must contain local ref, local object, remote ref, and remote object.");
                continue;
            }

            if (!IsObjectId(fields[1]) || !IsObjectId(fields[3]))
            {
                errors.Add($"Pre-push input line {lineNumber} contains an invalid Git object id.");
                continue;
            }

            updates.Add(new GitPushRefUpdate(fields[0], fields[1], fields[2], fields[3]));
        }

        return new GitPrePushParseResult(errors.Count == 0, updates, errors);
    }

    private static bool IsObjectId(string value)
        => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
