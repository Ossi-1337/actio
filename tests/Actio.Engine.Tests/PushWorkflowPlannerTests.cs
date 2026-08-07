using Actio.Core.Workflows;
using Actio.Engine.Triggers;

namespace Actio.Engine.Tests;

public sealed class PushWorkflowPlannerTests
{
    [Fact]
    public void Create_UsesDestinationReferenceAndChangedPaths()
    {
        var workflow = new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>(),
            [
                new WorkflowTrigger(
                    "push",
                    null,
                    new WorkflowTriggerFilters(["main"], [], [], [], ["src/**"], []))
            ]);
        var source = new PushWorkflowSource(".workflows/ci.yml", workflow);

        var dev = new PushReferenceEvent(
            "refs/heads/dev",
            "dev",
            "branch",
            "before",
            "after",
            ["src/App.cs"]);
        var mainDocs = dev with
        {
            FullReference = "refs/heads/main",
            ReferenceName = "main",
            ChangedPaths = ["README.md"]
        };
        var mainSource = mainDocs with { ChangedPaths = ["src/App.cs"] };

        Assert.Empty(PushWorkflowPlanner.Create([source], [dev]));
        Assert.Empty(PushWorkflowPlanner.Create([source], [mainDocs]));
        Assert.Single(PushWorkflowPlanner.Create([source], [mainSource]));
    }

    [Fact]
    public void Create_AddsEveryMatchingReferenceAndWorkflow()
    {
        var workflow = new WorkflowDocument(
            "All pushes",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>(),
            [new WorkflowTrigger("push", null)]);
        var references = new[]
        {
            new PushReferenceEvent("refs/heads/main", "main", "branch", "before", "after", []),
            new PushReferenceEvent("refs/tags/v1", "v1", "tag", "before", "after", [])
        };

        var plan = PushWorkflowPlanner.Create(
            [new PushWorkflowSource(".workflows/ci.yml", workflow)],
            references);

        Assert.Equal(2, plan.Count);
    }
}
