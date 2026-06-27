using System.Text;
using System.Text.RegularExpressions;

namespace Actio.Core.Workflows;

public sealed record WorkflowTriggerFilters(
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> BranchesIgnore,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> TagsIgnore,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> PathsIgnore)
{
    public static WorkflowTriggerFilters Empty { get; } = new([], [], [], [], [], []);

    public bool HasBranchFilters => Branches.Count > 0 || BranchesIgnore.Count > 0;

    public bool HasTagFilters => Tags.Count > 0 || TagsIgnore.Count > 0;

    public bool HasPathFilters => Paths.Count > 0 || PathsIgnore.Count > 0;
}

public sealed record WorkflowTriggerFilterContext(
    string EventName,
    string? Branch = null,
    string? Tag = null,
    IReadOnlyList<string>? ChangedPaths = null)
{
    public IReadOnlyList<string> ChangedPaths { get; init; } = ChangedPaths ?? [];
}

public sealed record WorkflowTriggerFilterDecision(
    bool Matches,
    string Reason);

public static class WorkflowTriggerFilterEvaluator
{
    public static WorkflowTriggerFilterDecision Evaluate(
        WorkflowTrigger trigger,
        WorkflowTriggerFilterContext context)
    {
        if (!string.Equals(trigger.EventName, context.EventName, StringComparison.Ordinal))
        {
            return No($"event '{context.EventName}' does not match workflow trigger '{trigger.EventName}'.");
        }

        var filters = trigger.Filters;
        var referenceDecision = EvaluateReferenceFilters(filters, context);
        if (!referenceDecision.Matches)
        {
            return referenceDecision;
        }

        var pathDecision = EvaluatePathFilters(filters, context.ChangedPaths);
        if (!pathDecision.Matches)
        {
            return pathDecision;
        }

        return Yes("trigger filters matched.");
    }

    private static WorkflowTriggerFilterDecision EvaluateReferenceFilters(
        WorkflowTriggerFilters filters,
        WorkflowTriggerFilterContext context)
    {
        if (!filters.HasBranchFilters && !filters.HasTagFilters)
        {
            return Yes("no branch or tag filters configured.");
        }

        if (!string.IsNullOrWhiteSpace(context.Branch))
        {
            if (!filters.HasBranchFilters)
            {
                return No("workflow trigger is filtered for tags, but the event has a branch ref.");
            }

            return EvaluateIncludedAndIgnored(
                context.Branch,
                filters.Branches,
                filters.BranchesIgnore,
                "branch");
        }

        if (!string.IsNullOrWhiteSpace(context.Tag))
        {
            if (!filters.HasTagFilters)
            {
                return No("workflow trigger is filtered for branches, but the event has a tag ref.");
            }

            return EvaluateIncludedAndIgnored(
                context.Tag,
                filters.Tags,
                filters.TagsIgnore,
                "tag");
        }

        return No("workflow trigger has branch or tag filters, but the event has no branch or tag ref.");
    }

    private static WorkflowTriggerFilterDecision EvaluatePathFilters(
        WorkflowTriggerFilters filters,
        IReadOnlyList<string> changedPaths)
    {
        if (!filters.HasPathFilters)
        {
            return Yes("no path filters configured.");
        }

        if (changedPaths.Count == 0)
        {
            return No("workflow trigger has path filters, but no changed paths were provided.");
        }

        if (filters.Paths.Count > 0)
        {
            return changedPaths.Any(path => WorkflowGlobMatcher.MatchesOrdered(filters.Paths, path))
                ? Yes("at least one changed path matched the path filters.")
                : No("no changed paths matched the path filters.");
        }

        if (filters.PathsIgnore.Count > 0 &&
            changedPaths.All(path => WorkflowGlobMatcher.MatchesOrdered(filters.PathsIgnore, path)))
        {
            return No("all changed paths matched the path ignore filters.");
        }

        return Yes("at least one changed path was not ignored.");
    }

    private static WorkflowTriggerFilterDecision EvaluateIncludedAndIgnored(
        string value,
        IReadOnlyList<string> includePatterns,
        IReadOnlyList<string> ignorePatterns,
        string kind)
    {
        if (includePatterns.Count > 0 && !WorkflowGlobMatcher.MatchesOrdered(includePatterns, value))
        {
            return No($"{kind} '{value}' did not match the configured {kind} filters.");
        }

        if (ignorePatterns.Count > 0 && WorkflowGlobMatcher.MatchesOrdered(ignorePatterns, value))
        {
            return No($"{kind} '{value}' matched the configured {kind} ignore filters.");
        }

        return Yes($"{kind} filters matched.");
    }

    private static WorkflowTriggerFilterDecision Yes(string reason)
        => new(true, reason);

    private static WorkflowTriggerFilterDecision No(string reason)
        => new(false, reason);
}

internal static class WorkflowGlobMatcher
{
    public static bool MatchesOrdered(IReadOnlyList<string> patterns, string value)
    {
        var matches = false;

        foreach (var pattern in patterns)
        {
            var normalizedPattern = Normalize(pattern);
            var isNegative = normalizedPattern.StartsWith('!');
            var patternText = isNegative ? normalizedPattern[1..] : normalizedPattern;

            if (patternText.Length == 0)
            {
                continue;
            }

            if (!Matches(patternText, value))
            {
                continue;
            }

            matches = !isNegative;
        }

        return matches;
    }

    private static bool Matches(string pattern, string value)
    {
        var expression = "^" + ConvertGlobToRegex(Normalize(pattern)) + "$";
        return Regex.IsMatch(Normalize(value), expression, RegexOptions.CultureInvariant);
    }

    private static string ConvertGlobToRegex(string pattern)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];

            if (current == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                    continue;
                }

                builder.Append("[^/]*");
                continue;
            }

            if (current == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            builder.Append(Regex.Escape(current.ToString()));
        }

        return builder.ToString();
    }

    private static string Normalize(string value)
        => value.Replace('\\', '/');
}
