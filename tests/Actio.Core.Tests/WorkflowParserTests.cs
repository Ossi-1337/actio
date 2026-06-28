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
            unknown-key: value
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.unknown-key is not supported.");
    }

    [Fact]
    public void Parse_AcceptsTopLevelCompatibilityKeywordsWithWarnings()
    {
        var result = Parse(
            """
            name: CI
            run-name: CI on ${{ github.ref }}
            on:
              push:
                branches:
                  - main
            permissions:
              contents: read
            env:
              DOTNET_NOLOGO: "true"
            defaults:
              run:
                shell: bash
            concurrency:
              group: ci-${{ github.ref }}
              cancel-in-progress: true
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("workflow.run-name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("workflow.on", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("workflow.permissions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("workflow.concurrency", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("workflow.defaults", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("bash", result.Workflow!.Defaults.Shell);
        var trigger = Assert.Single(result.Workflow!.Triggers);
        Assert.Equal("push", trigger.EventName);
        Assert.NotNull(trigger.Configuration);
        Assert.True(trigger.Configuration.Properties.ContainsKey("branches"));
    }

    [Fact]
    public void Parse_ValidatesTopLevelCompatibilityKeywordShapes()
    {
        var result = Parse(
            """
            name: CI
            run-name:
              value: CI
            on:
              - push
              - {}
            permissions:
              - contents
            defaults: bash
            concurrency:
              - ci
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.run-name must be a string.");
        Assert.Contains(result.Errors, error => error == "workflow.on[1] must be a string.");
        Assert.Contains(result.Errors, error => error == "workflow.permissions must be a string or a mapping.");
        Assert.Contains(result.Errors, error => error == "workflow.defaults must be a mapping.");
        Assert.Contains(result.Errors, error => error == "workflow.concurrency must be a string or a mapping.");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_AcceptsJobIdentityEnvAndDefaults()
    {
        var result = Parse(
            """
            name: CI
            defaults:
              run:
                shell: sh
                working-directory: src
            jobs:
              test:
                name: Run tests
                env:
                  DOTNET_NOLOGO: "true"
                defaults:
                  run:
                    shell: bash
                    working-directory: src/Actio.Core
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("sh", result.Workflow!.Defaults.Shell);
        Assert.Equal("src", result.Workflow.Defaults.WorkingDirectory);

        var job = Assert.Single(result.Workflow.Jobs.Values);
        Assert.Equal("test", job.Name);
        Assert.Equal("Run tests", job.DisplayName);
        Assert.Equal("true", job.Env["DOTNET_NOLOGO"]);
        Assert.Equal("bash", job.Defaults.Shell);
        Assert.Equal("src/Actio.Core", job.Defaults.WorkingDirectory);
    }

    [Fact]
    public void Parse_RejectsUnsupportedDefaults()
    {
        var result = Parse(
            """
            name: CI
            defaults:
              run:
                shell: pwsh
                working-directory: ../outside
            jobs:
              test:
                defaults:
                  run:
                    working-directory: /absolute
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.defaults.run.shell must be bash or sh.");
        Assert.Contains(result.Errors, error => error == "workflow.defaults.run.working-directory must be a relative path inside the workspace.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.defaults.run.working-directory must be a relative path inside the workspace.");
    }

    [Fact]
    public void Parse_AcceptsOnEventSequenceAsTriggerMetadata()
    {
        var result = Parse(
            """
            name: CI
            on:
              - push
              - pull_request
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["push", "pull_request"], result.Workflow!.Triggers.Select(trigger => trigger.EventName));
        Assert.Contains(result.Warnings, warning => warning.Contains("trigger metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsOnEventMapConfigurationAsTriggerMetadata()
    {
        var result = Parse(
            """
            name: CI
            on:
              pull_request:
                types:
                  - opened
                  - synchronize
                branches:
                  - main
                paths:
                  - src/**
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var trigger = Assert.Single(result.Workflow!.Triggers);
        Assert.Equal("pull_request", trigger.EventName);
        Assert.Equal("mapping", trigger.Configuration!.Kind);
        Assert.Equal(["opened", "synchronize"], trigger.ActivityTypes);
        Assert.Equal(["opened", "synchronize"], trigger.Configuration.Properties["types"].Items.Select(item => item.Value));
        Assert.Equal("main", Assert.Single(trigger.Configuration.Properties["branches"].Items).Value);
        Assert.Equal("src/**", Assert.Single(trigger.Configuration.Properties["paths"].Items).Value);
    }

    [Fact]
    public void Parse_WarnsForUnknownKnownEventActivityType()
    {
        var result = Parse(
            """
            name: CI
            on:
              pull_request:
                types:
                  - opened
                  - invented
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var trigger = Assert.Single(result.Workflow!.Triggers);
        Assert.Equal(["opened", "invented"], trigger.ActivityTypes);
        Assert.Contains(result.Warnings, warning => warning.Contains("unknown activity type 'invented'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_StoresBranchTagAndPathTriggerFilters()
    {
        var result = Parse(
            """
            name: CI
            on:
              push:
                branches:
                  - main
                  - releases/**
                  - "!releases/**-alpha"
                tags: v*
                paths:
                  - src/**
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var filters = Assert.Single(result.Workflow!.Triggers).Filters;
        Assert.Equal(["main", "releases/**", "!releases/**-alpha"], filters.Branches);
        Assert.Equal(["v*"], filters.Tags);
        Assert.Equal(["src/**"], filters.Paths);
    }

    [Fact]
    public void Parse_RejectsMutuallyExclusiveTriggerFilters()
    {
        var result = Parse(
            """
            name: CI
            on:
              push:
                branches:
                  - main
                branches-ignore:
                  - legacy/**
                paths:
                  - src/**
                paths-ignore:
                  - docs/**
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("cannot define both branches and branches-ignore", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("cannot define both paths and paths-ignore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsNonStringTriggerFilterPattern()
    {
        var result = Parse(
            """
            name: CI
            on:
              push:
                branches:
                  - main
                  - include: dev
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.on.push.branches[1] must be a string.");
    }

    [Fact]
    public void Parse_WarnsForPullRequestTargetSecurityBoundary()
    {
        var result = Parse(
            """
            name: CI
            on:
              pull_request_target:
                branches:
                  - main
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("pull_request_target is security-sensitive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsWorkflowDispatchInputsAsTriggerMetadata()
    {
        var result = Parse(
            """
            name: CI
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    description: Target environment
                    required: true
                    type: choice
                    options:
                      - staging
                      - production
                  dry-run:
                    type: boolean
                    default: "false"
            jobs:
              test:
                if: "${{ inputs.environment == 'staging' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var dispatch = Assert.Single(result.Workflow!.Triggers).Dispatch;
        Assert.Equal(["environment", "dry-run"], dispatch.Inputs.Keys);
        Assert.True(dispatch.Inputs["environment"].Required);
        Assert.Equal("choice", dispatch.Inputs["environment"].Type);
        Assert.Equal(["staging", "production"], dispatch.Inputs["environment"].Options);
        Assert.Equal("boolean", dispatch.Inputs["dry-run"].Type);
        Assert.Equal("false", dispatch.Inputs["dry-run"].Default);
    }

    [Fact]
    public void Parse_RejectsInvalidWorkflowDispatchInput()
    {
        var result = Parse(
            """
            name: CI
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    required: sometimes
                    type: choice
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_dispatch.inputs.environment.required must be true or false.");
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_dispatch.inputs.environment.options is required when type is choice.");
    }

    [Fact]
    public void Parse_RejectsUndeclaredWorkflowDispatchInputCondition()
    {
        var result = Parse(
            """
            name: CI
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    type: string
            jobs:
              test:
                if: "${{ inputs.missing == 'true' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("inputs.missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("is not declared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsScheduleCronMetadata()
    {
        var result = Parse(
            """
            name: CI
            on:
              schedule:
                - cron: "0 8 * * 1"
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var trigger = Assert.Single(result.Workflow!.Triggers);
        Assert.Equal("schedule", trigger.EventName);
        Assert.Equal("0 8 * * 1", Assert.Single(trigger.Schedules).Cron);
        Assert.Contains(result.Warnings, warning => warning.Contains("operating system scheduler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsInvalidScheduleCronMetadata()
    {
        var result = Parse(
            """
            name: CI
            on:
              schedule:
                - cron: "0 8 *"
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.on.schedule[0].cron must contain five cron fields.");
    }

    [Fact]
    public void Parse_WarnsForRepositoryDispatchMetadata()
    {
        var result = Parse(
            """
            name: CI
            on:
              repository_dispatch:
                types:
                  - deploy
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("repository dispatch webhooks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsUnsupportedTriggerConfigurationKey()
    {
        var result = Parse(
            """
            name: CI
            on:
              push:
                unsupported-filter:
                  - main
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.on.push.unsupported-filter is not supported in workflow trigger configuration.");
        Assert.Empty(result.Warnings);
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
    public void Parse_RejectsCheckoutWithUnsupportedWith()
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
                    with:
                      fetch-depth: "0"
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("with is not supported", StringComparison.OrdinalIgnoreCase));
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
    public void Parse_AcceptsGitHubEventPayloadConditionExpression()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                if: "${{ github.event.event_name == 'workflow_dispatch' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
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
