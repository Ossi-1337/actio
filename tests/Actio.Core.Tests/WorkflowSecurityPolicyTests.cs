using Actio.Core.Security;
using Actio.Core.Workflows;

namespace Actio.Core.Tests;

public sealed class WorkflowSecurityPolicyTests
{
    [Fact]
    public void Analyze_RecordsMutableAndPinnedExternalActions()
    {
        var workflow = ParseWorkflow(
            $$"""
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Mutable Docker action
                    uses: docker://node:22
                  - name: Pinned GitHub action
                    uses: owner/repo@{{new string('a', 40)}}
            """);

        var findings = WorkflowSecurityPolicy.Analyze(workflow);

        Assert.Contains(findings, finding =>
            finding.Severity == "warning" &&
            finding.Category == "external-action.mutable-ref" &&
            finding.Location == "workflow.jobs.test.steps[0].uses" &&
            finding.Reference == "docker://node:22" &&
            finding.MutablePart == "22");
        Assert.Contains(findings, finding =>
            finding.Severity == "info" &&
            finding.Category == "external-action.pinned-ref" &&
            finding.Location == "workflow.jobs.test.steps[1].uses" &&
            finding.IsPinned == true);
    }

    [Fact]
    public void Analyze_RecordsPullRequestTargetTrigger()
    {
        var workflow = ParseWorkflow(
            """
            name: CI
            on:
              pull_request_target:
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        var finding = Assert.Single(WorkflowSecurityPolicy.Analyze(workflow));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("unsafe-trigger", finding.Category);
        Assert.Equal("workflow.on.pull_request_target", finding.Location);
        Assert.Contains("security-sensitive", finding.Message);
    }

    private static WorkflowDocument ParseWorkflow(string yaml)
    {
        var result = new WorkflowParser().Parse(new StringReader(yaml));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Workflow!;
    }
}
