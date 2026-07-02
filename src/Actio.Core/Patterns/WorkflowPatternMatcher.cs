using System.Text;
using System.Text.RegularExpressions;

namespace Actio.Core.Patterns;

public sealed record WorkflowPatternOptions(bool IgnoreCase)
{
    public static WorkflowPatternOptions CaseSensitive { get; } = new(false);

    public static WorkflowPatternOptions CaseInsensitive { get; } = new(true);

    public static WorkflowPatternOptions CurrentPlatform => OperatingSystem.IsWindows()
        ? CaseInsensitive
        : CaseSensitive;

    internal RegexOptions RegexOptions => IgnoreCase
        ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
        : RegexOptions.CultureInvariant;
}

public sealed class WorkflowPattern
{
    private readonly PatternSegment[] _segments;

    internal WorkflowPattern(string pattern, WorkflowPatternOptions options)
    {
        Pattern = WorkflowPatternMatcher.NormalizePattern(pattern);
        _segments = Pattern.Length == 0
            ? []
            : Pattern.Split('/').Select(segment => PatternSegment.Create(segment, options)).ToArray();
    }

    public string Pattern { get; }

    public bool IsMatch(string value)
    {
        if (_segments.Length == 0)
        {
            return false;
        }

        var valueSegments = WorkflowPatternMatcher.NormalizeValue(value).Split('/');
        return IsSegmentMatch(patternIndex: 0, valueSegments, valueIndex: 0);
    }

    private bool IsSegmentMatch(
        int patternIndex,
        IReadOnlyList<string> valueSegments,
        int valueIndex)
    {
        if (patternIndex == _segments.Length)
        {
            return valueIndex == valueSegments.Count;
        }

        var segment = _segments[patternIndex];
        if (segment.IsRecursive)
        {
            if (patternIndex == _segments.Length - 1)
            {
                return true;
            }

            for (var index = valueIndex; index <= valueSegments.Count; index++)
            {
                if (IsSegmentMatch(patternIndex + 1, valueSegments, index))
                {
                    return true;
                }
            }

            return false;
        }

        return valueIndex < valueSegments.Count &&
            segment.IsMatch(valueSegments[valueIndex]) &&
            IsSegmentMatch(patternIndex + 1, valueSegments, valueIndex + 1);
    }

    private sealed class PatternSegment
    {
        private readonly Regex? _regex;

        private PatternSegment(bool isRecursive, Regex? regex)
        {
            IsRecursive = isRecursive;
            _regex = regex;
        }

        public bool IsRecursive { get; }

        public static PatternSegment Create(string segment, WorkflowPatternOptions options)
        {
            if (segment == "**")
            {
                return new PatternSegment(isRecursive: true, regex: null);
            }

            return new PatternSegment(isRecursive: false, CreateSegmentRegex(segment, options));
        }

        public bool IsMatch(string value)
            => _regex?.IsMatch(value) ?? false;

        private static Regex CreateSegmentRegex(string segment, WorkflowPatternOptions options)
        {
            var expression = "^" + ConvertSegmentToRegex(segment) + "$";
            return new Regex(expression, options.RegexOptions);
        }

        private static string ConvertSegmentToRegex(string segment)
        {
            var builder = new StringBuilder();

            foreach (var current in segment)
            {
                builder.Append(current switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    '+' => "[^/]+",
                    _ => Regex.Escape(current.ToString())
                });
            }

            return builder.ToString();
        }
    }
}

public static class WorkflowPatternMatcher
{
    public static WorkflowPattern Compile(string pattern)
        => Compile(pattern, WorkflowPatternOptions.CurrentPlatform);

    public static WorkflowPattern Compile(string pattern, WorkflowPatternOptions options)
        => new(pattern, options);

    public static bool Matches(string pattern, string value)
        => Matches(pattern, value, WorkflowPatternOptions.CurrentPlatform);

    public static bool Matches(
        string pattern,
        string value,
        WorkflowPatternOptions options)
        => Compile(pattern, options).IsMatch(value);

    public static bool MatchesOrdered(IReadOnlyList<string> patterns, string value)
        => MatchesOrdered(patterns, value, WorkflowPatternOptions.CurrentPlatform);

    public static bool MatchesOrdered(
        IReadOnlyList<string> patterns,
        string value,
        WorkflowPatternOptions options)
    {
        var matches = false;

        foreach (var pattern in patterns)
        {
            var normalizedPattern = NormalizePattern(pattern);
            var isNegative = normalizedPattern.StartsWith('!');
            var patternText = isNegative ? normalizedPattern[1..] : normalizedPattern;

            if (patternText.Length == 0)
            {
                continue;
            }

            if (Compile(patternText, options).IsMatch(value))
            {
                matches = !isNegative;
            }
        }

        return matches;
    }

    public static string NormalizePattern(string pattern)
        => pattern.Replace('\\', '/').Trim();

    public static string NormalizeValue(string value)
        => value.Replace('\\', '/');
}
