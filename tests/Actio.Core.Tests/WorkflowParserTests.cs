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
                artifacts:
                  - name: coverage
                    path: coverage.txt
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
        Assert.Equal("coverage.txt", result.Workflow.Jobs["prepare"].Artifacts[0].Path);
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
    public void Parse_AcceptsLocalUsesSteps()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Local action
                    uses: ./.actio/actions/hello
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("./.actio/actions/hello", result.Workflow!.Jobs["test"].Steps[0].Uses);
    }

    [Fact]
    public void Parse_AcceptsGitHubUsesAndWarnsForMutableRef()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Checkout
                    uses: actions/checkout@v4
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("mutable GitHub ref", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("actions/checkout@v4", result.Workflow!.Jobs["test"].Steps[0].Uses);
    }

    [Fact]
    public void Parse_AcceptsDockerUsesAndWarnsForMutableTag()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Node action
                    uses: docker://node:22
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("mutable Docker image reference", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("docker://node:22", result.Workflow!.Jobs["test"].Steps[0].Uses);
    }

    [Fact]
    public void Parse_AcceptsDockerDigestUsesWithoutMutableWarning()
    {
        var digest = new string('a', 64);
        var result = Parse(
            $$"""
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Pinned image action
                    uses: docker://node@sha256:{{digest}}
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_AcceptsGitHubCommitShaUsesWithoutMutableWarning()
    {
        var sha = new string('b', 40);
        var result = Parse(
            $$"""
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Pinned GitHub action
                    uses: owner/repo/action@{{sha}}
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_RejectsUnsupportedUsesReferenceShape()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Missing ref
                    uses: owner/repo
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Supported formats", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void Parse_RejectsConditionReferenceNotDeclaredInNeeds()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              prepare:
                runs-on: ubuntu-latest
                outputs:
                  changed: "true"
                steps:
                  - name: Prepare
                    run: dotnet restore
              test:
                if: "${{ needs.prepare.outputs.changed == 'true' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("not declared", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowParseResult Parse(string yaml)
    {
        return new WorkflowParser().Parse(new StringReader(yaml));
    }
}
