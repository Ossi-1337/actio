using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowEventPayloadTests
{
    [Fact]
    public void Create_ProvidesLocalGitDiffDefaults()
    {
        var payload = WorkflowEventPayload.Create("workflow_dispatch", "CLI");

        Assert.Equal("HEAD", payload.Properties["diff_base"]);
        Assert.Equal("false", payload.Properties["new_ref"]);
    }

    [Fact]
    public void Create_MergesExplicitPropertiesWithDefaults()
    {
        var payload = WorkflowEventPayload.Create(
            "push",
            "Git pre-push",
            properties: new Dictionary<string, string> { ["diff_base"] = "before-sha" });

        Assert.Equal("before-sha", payload.Properties["diff_base"]);
        Assert.Equal("false", payload.Properties["new_ref"]);
        Assert.Equal("push", payload.Properties["event_name"]);
    }
}
