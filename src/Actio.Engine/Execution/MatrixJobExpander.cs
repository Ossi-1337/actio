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
        var combinations = CreateCombinations(job.Strategy.Matrix.Axes);
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

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateCombinations(
        IReadOnlyDictionary<string, IReadOnlyList<string>> axes)
    {
        if (axes.Count == 0)
        {
            return [];
        }

        var combinations = new List<Dictionary<string, string>>
        {
            new(StringComparer.Ordinal)
        };

        foreach (var axis in axes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            combinations = combinations
                .SelectMany(existing => axis.Value.Select(value =>
                {
                    var next = new Dictionary<string, string>(existing, StringComparer.Ordinal)
                    {
                        [axis.Key] = value
                    };
                    return next;
                }))
                .ToList();
        }

        return combinations;
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
}

internal sealed record MatrixJobExpansionResult(
    IReadOnlyDictionary<string, WorkflowJob> Jobs,
    IReadOnlyDictionary<string, IReadOnlyList<string>> JobNamesByBaseName,
    IReadOnlyList<string> Errors);
