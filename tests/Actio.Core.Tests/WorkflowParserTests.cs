using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowParserTests
{
    [Fact]
    public void Parse_AcceptsValidWorkflow()
    {
        var result = Parse(
            """
            name: CI
            env:
              DOTNET_NOLOGO: "true"
            jobs:
              prepare:
                runs-on: ubuntu-latest
                outputs:
                  changed: "true"
                steps:
                  - name: Prepare
                    run: dotnet restore
              test:
                needs: prepare
                if: "${{ needs.prepare.outputs.changed == 'true' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("CI", result.Workflow!.Name);
        Assert.Equal(2, result.Workflow.Jobs.Count);
        Assert.Equal(2, result.Workflow.StepCount);
        Assert.Equal(["prepare"], result.Workflow.Jobs["test"].Needs);
    }

    [Fact]
    public void Parse_RequiresWorkflowName()
    {
        var result = Parse(
            """
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.name is required.");
    }

    [Fact]
    public void Parse_RejectsUnknownTopLevelKeys()
    {
        var result = Parse(
            """
            name: CI
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.on is not supported.");
    }

    [Fact]
    public void Parse_RejectsUsesStepsUntilFutureMilestone()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Setup .NET
                    uses: setup-dotnet
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("future uses/cache extensibility milestone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsUnknownNeeds()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                needs: prepare
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("references unknown job 'prepare'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsUnsupportedConditionExpression()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                if: "${{ always() }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unsupported expression", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowParseResult Parse(string yaml)
    {
        return new WorkflowParser().Parse(new StringReader(yaml));
    }
}
