using Actio.Core.Workflows;
using Actio.Engine.Execution;

namespace Actio.Engine.Tests;

public sealed class MatrixJobExpanderTests
{
    [Fact]
    public void Expand_ProducesNoJobWhenEveryMatrixCombinationIsExcluded()
    {
        var job = CreateJob(
            new WorkflowJobMatrix(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["target"] = ["production"]
                },
                Exclude:
                [
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["target"] = "production"
                    }
                ]));

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal) { [job.Name] = job });

        Assert.Empty(result.Errors);
        Assert.Empty(result.Jobs);
        Assert.Empty(result.JobNamesByBaseName[job.Name]);
    }

    [Fact]
    public void Expand_RejectsDependencyOnMatrixWithNoGeneratedVariants()
    {
        var matrixJob = CreateJob(
            new WorkflowJobMatrix(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["target"] = ["production"]
                },
                Exclude:
                [
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["target"] = "production"
                    }
                ]));
        var dependentJob = CreateJob(WorkflowJobMatrix.Empty, "publish", ["test"]);

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal)
            {
                [matrixJob.Name] = matrixJob,
                [dependentJob.Name] = dependentJob
            });

        Assert.Contains(result.Errors, error =>
            error.Contains("matrix job 'test' with no generated variants", StringComparison.Ordinal));
    }

    [Fact]
    public void Expand_AddsStandaloneIncludeAfterEveryAxisCombinationIsExcluded()
    {
        var job = CreateJob(
            new WorkflowJobMatrix(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["target"] = ["production"]
                },
                Include:
                [
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["target"] = "staging"
                    }
                ],
                Exclude:
                [
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["target"] = "production"
                    }
                ]));

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal) { [job.Name] = job });

        Assert.Empty(result.Errors);
        var expanded = Assert.Single(result.Jobs.Values);
        Assert.Equal("staging", expanded.Matrix["target"]);
    }

    [Fact]
    public void Expand_RejectsCartesianProductAboveLimitBeforeMaterialization()
    {
        var values = Enumerable.Range(0, 17).Select(value => value.ToString()).ToArray();
        var job = CreateJob(
            new WorkflowJobMatrix(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["first"] = values,
                    ["second"] = values
                }));

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal) { [job.Name] = job });

        Assert.Empty(result.Jobs);
        Assert.Contains(result.Errors, error =>
            error == $"workflow.jobs.test.strategy.matrix cannot generate more than {MatrixJobExpander.MaximumGeneratedJobs} jobs.");
    }

    [Fact]
    public void Expand_AcceptsCartesianProductAtLimit()
    {
        var values = Enumerable.Range(0, 16).Select(value => value.ToString()).ToArray();
        var job = CreateJob(
            new WorkflowJobMatrix(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["first"] = values,
                    ["second"] = values
                }));

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal) { [job.Name] = job });

        Assert.Empty(result.Errors);
        Assert.Equal(MatrixJobExpander.MaximumGeneratedJobs, result.Jobs.Count);
    }

    [Fact]
    public void Expand_RejectsIncludeOnlyMatrixAboveLimit()
    {
        var include = Enumerable.Range(0, MatrixJobExpander.MaximumGeneratedJobs + 1)
            .Select(value => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["value"] = value.ToString()
            })
            .ToArray();
        var job = CreateJob(new WorkflowJobMatrix(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            Include: include));

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal) { [job.Name] = job });

        Assert.Empty(result.Jobs);
        Assert.Contains(result.Errors, error =>
            error.Contains($"more than {MatrixJobExpander.MaximumGeneratedJobs} jobs", StringComparison.Ordinal));
    }

    [Fact]
    public void Expand_RejectsManyAxesWithoutOverflowingCombinationCount()
    {
        var axes = Enumerable.Range(0, 64).ToDictionary(
            index => $"axis-{index}",
            _ => (IReadOnlyList<string>)["first", "second"],
            StringComparer.Ordinal);
        var job = CreateJob(new WorkflowJobMatrix(axes));

        var result = MatrixJobExpander.Expand(
            new Dictionary<string, WorkflowJob>(StringComparer.Ordinal) { [job.Name] = job });

        Assert.Empty(result.Jobs);
        Assert.Single(result.Errors);
    }

    private static WorkflowJob CreateJob(
        WorkflowJobMatrix matrix,
        string name = "test",
        IReadOnlyList<string>? needs = null)
    {
        return new WorkflowJob(
            name,
            null,
            needs ?? [],
            null,
            "ubuntu-latest",
            new Dictionary<string, string>(),
            WorkflowRunDefaults.Empty,
            null,
            false,
            null,
            new WorkflowJobStrategy(matrix),
            new Dictionary<string, string>(),
            [],
            [new WorkflowStep("Run", "true", null)]);
    }
}
