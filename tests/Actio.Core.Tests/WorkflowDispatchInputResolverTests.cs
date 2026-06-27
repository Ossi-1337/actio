using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowDispatchInputResolverTests
{
    [Fact]
    public void Resolve_AppliesDefaultsAndProvidedInputs()
    {
        var workflow = CreateWorkflow(new Dictionary<string, WorkflowDispatchInput>
        {
            ["environment"] = new("environment", null, true, null, "choice", ["staging", "production"]),
            ["dry-run"] = new("dry-run", null, false, "false", "boolean", [])
        });

        var result = WorkflowDispatchInputResolver.Resolve(
            workflow,
            new Dictionary<string, string> { ["environment"] = "staging" });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("staging", result.Inputs["environment"]);
        Assert.Equal("false", result.Inputs["dry-run"]);
    }

    [Fact]
    public void Resolve_RejectsMissingRequiredInput()
    {
        var workflow = CreateWorkflow(new Dictionary<string, WorkflowDispatchInput>
        {
            ["environment"] = new("environment", null, true, null, "string", [])
        });

        var result = WorkflowDispatchInputResolver.Resolve(workflow, new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("environment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_RejectsEmptyRequiredInput()
    {
        var workflow = CreateWorkflow(new Dictionary<string, WorkflowDispatchInput>
        {
            ["environment"] = new("environment", null, true, null, "string", [])
        });

        var result = WorkflowDispatchInputResolver.Resolve(
            workflow,
            new Dictionary<string, string> { ["environment"] = string.Empty });

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("environment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_RejectsUnknownInput()
    {
        var workflow = CreateWorkflow(new Dictionary<string, WorkflowDispatchInput>());

        var result = WorkflowDispatchInputResolver.Resolve(
            workflow,
            new Dictionary<string, string> { ["unknown"] = "value" });

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("not declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_RejectsInvalidChoice()
    {
        var workflow = CreateWorkflow(new Dictionary<string, WorkflowDispatchInput>
        {
            ["environment"] = new("environment", null, true, null, "choice", ["staging", "production"])
        });

        var result = WorkflowDispatchInputResolver.Resolve(
            workflow,
            new Dictionary<string, string> { ["environment"] = "dev" });

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("staging, production", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowDocument CreateWorkflow(IReadOnlyDictionary<string, WorkflowDispatchInput> inputs)
    {
        return new WorkflowDocument(
            "CI",
            new Dictionary<string, string>(),
            new Dictionary<string, WorkflowJob>
            {
                ["test"] = new(
                    "test",
                    [],
                    null,
                    "ubuntu-latest",
                    new Dictionary<string, string>(),
                    [new WorkflowStep("Test", "dotnet test", null)])
            },
            [new WorkflowTrigger("workflow_dispatch", null, Dispatch: new WorkflowDispatch(inputs))]);
    }
}
