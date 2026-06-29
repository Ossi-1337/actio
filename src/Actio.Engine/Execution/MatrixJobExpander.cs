using Actio.Core.Expressions;
using Actio.Core.Workflows;

namespace Actio.Engine.Execution;

internal static class MatrixJobExpander
{
    public static MatrixJobExpansionResult Expand(IReadOnlyDictionary<string, WorkflowJob> jobs)
    {
        var errors = new List<string>();
        var expandedByBaseName = new Dictionary<string, List<WorkflowJob>>(StringComparer.Ordinal);

        foreach (var job in jobs.Values)
        {
            expandedByBaseName[job.Name] = ExpandJob(job, errors);
        }

        var expandedJobs = new Dictionary<string, WorkflowJob>(StringComparer.Ordinal);
        foreach (var job in jobs.Values)
        {
            foreach (var expandedJob in expandedByBaseName[job.Name])
            {
                var needs = ExpandNeeds(job, expandedByBaseName, errors);
                var resolvedJob = expandedJob with { Needs = needs };
                if (!expandedJobs.TryAdd(resolvedJob.Name, resolvedJob))
                {
                    errors.Add($"workflow.jobs.{job.Name}.strategy.matrix creates duplicate expanded job '{resolvedJob.Name}'.");
                }
            }
        }

        var namesByBaseName = expandedByBaseName.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.Select(job => job.Name).ToArray(),
            StringComparer.Ordinal);

        return new MatrixJobExpansionResult(expandedJobs, namesByBaseName, errors);
    }

    private static List<WorkflowJob> ExpandJob(WorkflowJob job, List<string> errors)
    {
        var combinations = CreateCombinations(job.Strategy.Matrix);
        if (combinations.Count == 0)
        {
            return
            [
                job with
                {
                    BaseName = job.Name,
                    LogicalNeeds = job.Needs,
                    Matrix = new Dictionary<string, string>()
                }
            ];
        }

        return combinations
            .Select(combination => CreateExpandedJob(job, combination, errors))
            .ToList();
    }

    private static WorkflowJob CreateExpandedJob(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> matrix,
        List<string> errors)
    {
        return job with
        {
            Name = FormatExpandedJobName(job.Name, matrix),
            BaseName = job.Name,
            DisplayName = FormatExpandedDisplayName(job.DisplayName, matrix),
            LogicalNeeds = job.Needs,
            RunsOn = ResolveRunsOn(job, matrix, errors),
            Matrix = matrix
        };
    }

    private static IReadOnlyList<string> ExpandNeeds(
        WorkflowJob job,
        IReadOnlyDictionary<string, List<WorkflowJob>> expandedByBaseName,
        List<string> errors)
    {
        var needs = new List<string>();
        foreach (var neededJob in job.Needs)
        {
            if (!expandedByBaseName.TryGetValue(neededJob, out var expandedNeeds))
            {
                errors.Add($"workflow.jobs.{job.Name}.needs references unknown job '{neededJob}'.");
                continue;
            }

            needs.AddRange(expandedNeeds.Select(item => item.Name));
        }

        return needs;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateCombinations(WorkflowJobMatrix matrix)
    {
        var axisNames = matrix.Axes.Keys.ToHashSet(StringComparer.Ordinal);
        var combinations = CreateAxisCombinations(matrix.Axes);

        ApplyExcludeEntries(combinations, matrix.Exclude);
        ApplyIncludeEntries(combinations, axisNames, matrix.Include);

        return combinations.Select(combination => combination.Values).ToArray();
    }

    private static List<MatrixCombination> CreateAxisCombinations(
        IReadOnlyDictionary<string, IReadOnlyList<string>> axes)
    {
        if (axes.Count == 0)
        {
            return [];
        }

        var combinations = new List<MatrixCombination>
        {
            MatrixCombination.FromAxisValues(new Dictionary<string, string>(StringComparer.Ordinal))
        };

        foreach (var axis in axes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            combinations = combinations
                .SelectMany(existing => axis.Value.Select(value =>
                {
                    var next = new Dictionary<string, string>(existing.Values, StringComparer.Ordinal)
                    {
                        [axis.Key] = value
                    };
                    return MatrixCombination.FromAxisValues(next);
                }))
                .ToList();
        }

        return combinations;
    }

    private static void ApplyIncludeEntries(
        List<MatrixCombination> combinations,
        IReadOnlySet<string> axisNames,
        IReadOnlyList<IReadOnlyDictionary<string, string>> includeEntries)
    {
        foreach (var includeEntry in includeEntries)
        {
            var matches = combinations
                .Where(combination => combination.HasAxisValues && CanApplyInclude(combination, axisNames, includeEntry))
                .ToArray();

            if (matches.Length == 0)
            {
                combinations.Add(MatrixCombination.FromIncludeValues(includeEntry));
                continue;
            }

            foreach (var match in matches)
            {
                match.Merge(includeEntry);
            }
        }
    }

    private static bool CanApplyInclude(
        MatrixCombination combination,
        IReadOnlySet<string> axisNames,
        IReadOnlyDictionary<string, string> includeEntry)
    {
        foreach (var item in includeEntry)
        {
            if (!axisNames.Contains(item.Key))
            {
                continue;
            }

            if (!combination.AxisValues.TryGetValue(item.Key, out var axisValue) ||
                !string.Equals(axisValue, item.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyExcludeEntries(
        List<MatrixCombination> combinations,
        IReadOnlyList<IReadOnlyDictionary<string, string>> excludeEntries)
    {
        if (excludeEntries.Count == 0)
        {
            return;
        }

        combinations.RemoveAll(combination =>
            excludeEntries.Any(excludeEntry => IsMatch(combination.Values, excludeEntry)));
    }

    private static bool IsMatch(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> match)
    {
        return match.All(item =>
            values.TryGetValue(item.Key, out var value) &&
            string.Equals(value, item.Value, StringComparison.Ordinal));
    }

    private static string ResolveRunsOn(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> matrix,
        List<string> errors)
    {
        if (!job.RunsOn.Contains("${{", StringComparison.Ordinal))
        {
            return job.RunsOn;
        }

        var contextData = new ExpressionContextData(
            [ExpressionContextRoot.AvailableRoot("matrix", ExpressionContextData.FromStrings(matrix), allowMissingProperties: false)],
            null);
        var interpolation = ExpressionTemplate.Interpolate(
            job.RunsOn,
            new ExpressionEvaluationContext(contextData.Resolve));

        if (interpolation.Success)
        {
            return interpolation.Value;
        }

        errors.Add($"workflow.jobs.{job.Name}.runs-on could not be evaluated: {string.Join(" ", interpolation.Errors)}");
        return job.RunsOn;
    }

    private static string FormatExpandedJobName(
        string jobName,
        IReadOnlyDictionary<string, string> matrix)
    {
        return $"{jobName}[{FormatMatrixIdentity(matrix)}]";
    }

    private static string FormatExpandedDisplayName(
        string displayName,
        IReadOnlyDictionary<string, string> matrix)
    {
        return $"{displayName} ({FormatMatrixSummary(matrix)})";
    }

    private static string FormatMatrixIdentity(IReadOnlyDictionary<string, string> matrix)
    {
        return string.Join(",", matrix
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={ToIdentityValue(item.Value)}"));
    }

    private static string FormatMatrixSummary(IReadOnlyDictionary<string, string> matrix)
    {
        return string.Join(", ", matrix
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}: {item.Value}"));
    }

    private static string ToIdentityValue(string value)
    {
        var characters = value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_')
            .ToArray();
        var sanitized = new string(characters);
        return string.IsNullOrWhiteSpace(sanitized) ? "value" : sanitized;
    }

    private sealed class MatrixCombination
    {
        private MatrixCombination(
            IReadOnlyDictionary<string, string> axisValues,
            IReadOnlyDictionary<string, string> values)
        {
            AxisValues = axisValues;
            Values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, string> AxisValues { get; }

        public Dictionary<string, string> Values { get; }

        public bool HasAxisValues => AxisValues.Count > 0;

        public static MatrixCombination FromAxisValues(IReadOnlyDictionary<string, string> values)
        {
            return new MatrixCombination(
                new Dictionary<string, string>(values, StringComparer.Ordinal),
                values);
        }

        public static MatrixCombination FromIncludeValues(IReadOnlyDictionary<string, string> values)
        {
            return new MatrixCombination(
                new Dictionary<string, string>(),
                values);
        }

        public void Merge(IReadOnlyDictionary<string, string> values)
        {
            foreach (var item in values)
            {
                Values[item.Key] = item.Value;
            }
        }
    }
}

internal sealed record MatrixJobExpansionResult(
    IReadOnlyDictionary<string, WorkflowJob> Jobs,
    IReadOnlyDictionary<string, IReadOnlyList<string>> JobNamesByBaseName,
    IReadOnlyList<string> Errors);
