using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowTriggerFilterEvaluatorTests
{
    [Fact]
    public void Evaluate_MatchesBranchIncludeFilter()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            ["main", "releases/**", "feature/+"],
            [],
            [],
            [],
            [],
            []));

        var decision = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "releases/1.0"));
        var featureDecision = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "feature/login"));

        Assert.True(decision.Matches, decision.Reason);
        Assert.True(featureDecision.Matches, featureDecision.Reason);
    }

    [Fact]
    public void Evaluate_RejectsBranchIgnoreFilter()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            [],
            ["legacy/**"],
            [],
            [],
            [],
            []));

        var decision = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "legacy/old"));

        Assert.False(decision.Matches);
    }

    [Fact]
    public void Evaluate_UsesOrderedNegativePatterns()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            ["releases/**", "!releases/**-alpha", "releases/special-alpha"],
            [],
            [],
            [],
            [],
            []));

        var rejected = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "releases/1.0-alpha"));
        var reIncluded = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "releases/special-alpha"));

        Assert.False(rejected.Matches);
        Assert.True(reIncluded.Matches, reIncluded.Reason);
    }

    [Fact]
    public void Evaluate_MatchesTagIncludeFilter()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            [],
            [],
            ["v*"],
            [],
            [],
            []));

        var decision = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Tag: "v1.2.3"));

        Assert.True(decision.Matches, decision.Reason);
    }

    [Fact]
    public void Evaluate_RejectsBranchEventWhenOnlyTagFiltersExist()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            [],
            [],
            ["v*"],
            [],
            [],
            []));

        var decision = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "main"));

        Assert.False(decision.Matches);
    }

    [Fact]
    public void Evaluate_MatchesPathFiltersAgainstChangedFiles()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            [],
            [],
            [],
            [],
            [@"src\**", "!src/docs/**"],
            []));

        var accepted = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "main", ChangedPaths: ["docs/readme.md", "src/app/Program.cs"]));
        var rejected = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "main", ChangedPaths: ["src/docs/readme.md"]));

        Assert.True(accepted.Matches, accepted.Reason);
        Assert.False(rejected.Matches);
    }

    [Fact]
    public void Evaluate_PathsIgnoreSkipsOnlyWhenAllChangedFilesAreIgnored()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            [],
            [],
            [],
            [],
            [],
            ["docs/**", "*.md"]));

        var ignored = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "main", ChangedPaths: ["docs/readme.md", "README.md"]));
        var accepted = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Branch: "main", ChangedPaths: ["docs/readme.md", "src/app.cs"]));

        Assert.False(ignored.Matches);
        Assert.True(accepted.Matches, accepted.Reason);
    }

    [Fact]
    public void Evaluate_IgnoresPathFiltersForTagPushes()
    {
        var trigger = CreateTrigger(new WorkflowTriggerFilters(
            [],
            [],
            ["v*"],
            [],
            ["src/**"],
            []));

        var decision = WorkflowTriggerFilterEvaluator.Evaluate(
            trigger,
            new WorkflowTriggerFilterContext("push", Tag: "v1.0.0"));

        Assert.True(decision.Matches, decision.Reason);
    }

    private static WorkflowTrigger CreateTrigger(WorkflowTriggerFilters filters)
        => new("push", null, filters);
}
