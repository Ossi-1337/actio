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
    public void Parse_AcceptsJobControls()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              deploy:
                runs-on: ubuntu-latest
                timeout-minutes: 5
                continue-on-error: true
                concurrency:
                  group: deploy-main
                  cancel-in-progress: true
                steps:
                  - name: Deploy
                    run: ./deploy.sh
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var job = result.Workflow!.Jobs["deploy"];
        Assert.Equal(5, job.TimeoutMinutes);
        Assert.True(job.ContinueOnError);
        Assert.NotNull(job.Concurrency);
        Assert.Equal("deploy-main", job.Concurrency.Group);
        Assert.True(job.Concurrency.CancelInProgress);
    }

    [Fact]
    public void Parse_AcceptsJobContainer()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                container:
                  image: node:22
                  env:
                    NODE_ENV: test
                  ports:
                    - 3000:3000
                  volumes:
                    - ./.actio/cache:/cache:ro
                  options: --cpus 1 --memory=512m --init
                steps:
                  - name: Test
                    run: npm test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var container = result.Workflow!.Jobs["test"].Container;
        Assert.NotNull(container);
        Assert.Equal("node:22", container.Image);
        Assert.Equal("test", container.Env["NODE_ENV"]);
        Assert.Equal(["3000:3000"], container.Ports);
        var volume = Assert.Single(container.Volumes);
        Assert.Equal("./.actio/cache", volume.Source);
        Assert.Equal("/cache", volume.Target);
        Assert.True(volume.ReadOnly);
        Assert.Equal(["--cpus", "1", "--memory=512m", "--init"], container.Options);
    }

    [Fact]
    public void Parse_AcceptsScalarJobContainer()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                container: node:22
                steps:
                  - name: Test
                    run: npm test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("node:22", result.Workflow!.Jobs["test"].Container?.Image);
    }

    [Fact]
    public void Parse_RejectsUnsafeJobContainerValues()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                container:
                  env: not-a-map
                  ports:
                    - "--privileged"
                  volumes:
                    - ../outside:/cache
                    - ./cache:cache
                    - ./state:/actio/env
                    - ./cache:/cache:shared
                  options: --privileged --cpus --memory=
                  credentials:
                    username: oskar
                steps:
                  - name: Test
                    run: npm test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.container.image is required.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.container.env must be a mapping.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.container.credentials is not supported.");
        Assert.Contains(result.Errors, error => error.Contains("invalid Docker port mapping", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("source must be a relative path inside the workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("target must be an absolute container path outside /actio/env", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("mode must be ro or rw", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("unsupported Docker option '--privileged'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("option '--cpus' requires a value", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("option '--memory' requires a value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsJobServices()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                services:
                  postgres:
                    image: postgres:16
                    env:
                      POSTGRES_PASSWORD: postgres
                    ports:
                      - 5432:5432
                    volumes:
                      - ./db:/var/lib/postgresql/data
                    options: --health-cmd=pg_isready --health-interval=5s --health-timeout=3s --health-retries=5
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var service = Assert.Single(result.Workflow!.Jobs["test"].Services);
        Assert.Equal("postgres", service.Key);
        Assert.Equal("postgres:16", service.Value.Image);
        Assert.Equal("postgres", service.Value.Env["POSTGRES_PASSWORD"]);
        Assert.Equal(["5432:5432"], service.Value.Ports);
        var volume = Assert.Single(service.Value.Volumes);
        Assert.Equal("./db", volume.Source);
        Assert.Equal("/var/lib/postgresql/data", volume.Target);
        Assert.False(volume.ReadOnly);
        Assert.Equal(
            ["--health-cmd=pg_isready", "--health-interval=5s", "--health-timeout=3s", "--health-retries=5"],
            service.Value.Options);
    }

    [Fact]
    public void Parse_RejectsUnsafeJobServices()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                services:
                  - postgres
                steps:
                  - name: Test
                    run: dotnet test
              unsafe:
                runs-on: ubuntu-latest
                services:
                  -bad:
                    image: postgres:16
                  redis:
                    ports:
                      - "6379 6379"
                    volumes:
                      - ../outside:/data
                    options: --network host
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.services must be a mapping.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.unsafe.services.-bad must use a Docker-safe service name.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.unsafe.services.redis.image is required.");
        Assert.Contains(result.Errors, error => error.Contains("invalid Docker port mapping", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("source must be a relative path inside the workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("unsupported Docker option '--network'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsMatrixStrategy()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                strategy:
                  fail-fast: false
                  max-parallel: 2
                  matrix:
                    os:
                      - ubuntu-latest
                      - debian-latest
                    dotnet:
                      - "10.0"
                    include:
                      - os: ubuntu-latest
                        configuration: Debug
                    exclude:
                      - os: debian-latest
                        dotnet: "10.0"
                runs-on: ${{ matrix.os }}
                steps:
                  - name: Test
                    if: "${{ matrix.dotnet == '10.0' }}"
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var strategy = result.Workflow!.Jobs["test"].Strategy;
        Assert.False(strategy.FailFast);
        Assert.Equal(2, strategy.MaxParallel);

        var matrix = strategy.Matrix;
        Assert.Equal(["ubuntu-latest", "debian-latest"], matrix.Axes["os"]);
        Assert.Equal(["10.0"], matrix.Axes["dotnet"]);
        Assert.Equal("Debug", Assert.Single(matrix.Include)["configuration"]);
        Assert.Equal("debian-latest", Assert.Single(matrix.Exclude)["os"]);
    }

    [Fact]
    public void Parse_RejectsInvalidMatrixStrategy()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                strategy:
                  fail-fast: sometimes
                  max-parallel: 0
                  matrix:
                    os: ubuntu-latest
                    include:
                      - os:
                          nested: value
                    exclude:
                      - ubuntu-latest
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
              empty_strategy:
                strategy: {}
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.strategy.fail-fast must be true or false.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.strategy.max-parallel must be a positive integer.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.strategy.matrix.os must be a list of scalar values.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.strategy.matrix.include[0].os must be a scalar value.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.strategy.matrix.exclude[0] must be a mapping.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.empty_strategy.strategy.matrix is required.");
    }

    [Fact]
    public void Parse_AcceptsScalarJobConcurrency()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              deploy:
                runs-on: ubuntu-latest
                concurrency: deploy-main
                steps:
                  - name: Deploy
                    run: ./deploy.sh
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var concurrency = result.Workflow!.Jobs["deploy"].Concurrency;
        Assert.NotNull(concurrency);
        Assert.Equal("deploy-main", concurrency.Group);
        Assert.False(concurrency.CancelInProgress);
    }

    [Fact]
    public void Parse_RejectsInvalidJobControls()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              deploy:
                runs-on: ubuntu-latest
                timeout-minutes: 0
                continue-on-error: sometimes
                concurrency:
                  cancel-in-progress: later
                steps:
                  - name: Deploy
                    run: ./deploy.sh
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.deploy.timeout-minutes must be a positive integer.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.deploy.continue-on-error must be true or false.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.deploy.concurrency.group is required.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.deploy.concurrency.cancel-in-progress must be true or false.");
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
    public void Parse_AcceptsStepIdentityEnvShellAndWorkingDirectory()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - id: run_tests
                    name: Run tests
                    if: "${{ success() }}"
                    run: dotnet test
                    env:
                      DOTNET_NOLOGO: "true"
                    shell: bash
                    working-directory: src/Actio.Core
                    timeout-minutes: 10
                    continue-on-error: true
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var step = Assert.Single(result.Workflow!.Jobs["test"].Steps);
        Assert.Equal("run_tests", step.Id);
        Assert.Equal("${{ success() }}", step.If);
        Assert.Equal("true", step.Env["DOTNET_NOLOGO"]);
        Assert.Equal("bash", step.Shell);
        Assert.Equal("src/Actio.Core", step.WorkingDirectory);
        Assert.Equal(10, step.TimeoutMinutes);
        Assert.True(step.ContinueOnError);
    }

    [Fact]
    public void Parse_AcceptsActionStepWithInputs()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Use action
                    uses: ./.actio/actions/hello
                    with:
                      name: Actio
                      punctuation: "!"
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var step = Assert.Single(result.Workflow!.Jobs["test"].Steps);
        Assert.Equal("Actio", step.With["name"]);
        Assert.Equal("!", step.With["punctuation"]);
    }

    [Fact]
    public void Parse_RejectsInvalidStepIdentityAndExecutionSettings()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - id: 1bad
                    name: First
                    run: echo first
                    shell: pwsh
                    working-directory: ../outside
                    timeout-minutes: 0
                    continue-on-error: maybe
                    with:
                      name: Actio
                  - id: build
                    name: Build
                    run: dotnet build
                  - id: build
                    name: Duplicate
                    uses: actions/checkout@v4
                    shell: bash
                    working-directory: src
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("workflow.jobs.test.steps[0].id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[0].shell must be bash or sh.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[0].working-directory must be a relative path inside the workspace.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[0].timeout-minutes must be a positive integer.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[0].continue-on-error must be true or false.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[0].with is supported only for uses steps.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[2].id 'build' is already used in this job.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[2].shell is supported only for run steps.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.test.steps[2].working-directory is supported only for run steps.");
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
        Assert.Contains(result.Errors, error => error.Contains("no workflow_dispatch or workflow_call input named 'missing'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsWorkflowCallContract()
    {
        var result = Parse(
            """
            name: Reusable Build
            on:
              workflow_call:
                inputs:
                  configuration:
                    description: Build configuration
                    required: true
                    type: string
                  publish:
                    type: boolean
                    default: "false"
                secrets:
                  nuget-token:
                    description: NuGet token
                    required: false
                outputs:
                  package-path:
                    description: Package path
                    value: "${{ jobs.build.outputs.package-path }}"
            jobs:
              build:
                if: "${{ inputs.configuration == 'Release' }}"
                runs-on: ubuntu-latest
                outputs:
                  package-path: dist/app.zip
                steps:
                  - name: Build
                    run: dotnet build
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var trigger = Assert.Single(result.Workflow!.Triggers);
        Assert.Equal("workflow_call", trigger.EventName);
        Assert.Equal(["configuration", "publish"], trigger.Call.Inputs.Keys);
        Assert.True(trigger.Call.Inputs["configuration"].Required);
        Assert.Equal("string", trigger.Call.Inputs["configuration"].Type);
        Assert.Equal("boolean", trigger.Call.Inputs["publish"].Type);
        Assert.Equal("false", trigger.Call.Inputs["publish"].Default);
        Assert.Equal(["nuget-token"], trigger.Call.Secrets.Keys);
        Assert.False(trigger.Call.Secrets["nuget-token"].Required);
        Assert.Equal(["package-path"], trigger.Call.Outputs.Keys);
        Assert.Equal("${{ jobs.build.outputs.package-path }}", trigger.Call.Outputs["package-path"].Value);
        Assert.True(result.Workflow.IsReusableOnly);
    }

    [Fact]
    public void Parse_RejectsInvalidWorkflowCallContract()
    {
        var result = Parse(
            """
            name: Reusable Build
            on:
              workflow_call:
                inputs:
                  configuration:
                    required: maybe
                    default: true
                  publish:
                    type: boolean
                    default: maybe
                  retries:
                    type: number
                    default: many
                secrets:
                  nuget-token:
                    required: sometimes
                outputs:
                  package-path:
                    description: Missing value
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_call.inputs.configuration.required must be true or false.");
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_call.inputs.configuration.type is required.");
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_call.inputs.publish.default must be true or false when type is boolean.");
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_call.inputs.retries.default must be a number when type is number.");
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_call.secrets.nuget-token.required must be true or false.");
        Assert.Contains(result.Errors, error => error == "workflow.on.workflow_call.outputs.package-path.value is required.");
    }

    [Fact]
    public void Parse_RejectsUnsupportedWorkflowCallTriggerConfigurationKey()
    {
        var result = Parse(
            """
            name: Reusable Build
            on:
              workflow_call:
                branches:
                  - main
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error == "workflow.on.workflow_call.branches is not supported in workflow_call reusable workflow definitions.");
    }

    [Fact]
    public void Parse_AcceptsWorkflowCallInputsInConditions()
    {
        var result = Parse(
            """
            name: Reusable Build
            on:
              workflow_call:
                inputs:
                  configuration:
                    type: string
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    if: "${{ inputs.configuration == 'Release' }}"
                    run: dotnet build
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Parse_AcceptsReusableWorkflowCallerJob()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              build:
                uses: ./.workflows/reusable-build.yml
                with:
                  configuration: Release
                secrets:
                  nuget-token: local-token
              publish:
                needs: build
                if: "${{ needs.build.outputs.package-path == 'dist/app.zip' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Publish
                    run: echo publish
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var build = result.Workflow!.Jobs["build"];
        Assert.True(build.IsReusableWorkflowCall);
        Assert.Equal("reusable-workflow", build.RunsOn);
        Assert.Equal("./.workflows/reusable-build.yml", build.Call!.Uses);
        Assert.Equal("Release", build.Call.With["configuration"]);
        Assert.Equal("local-token", build.Call.Secrets["nuget-token"]);
        Assert.Empty(build.Steps);
        Assert.Equal(1, build.ExecutionStepCount);
    }

    [Fact]
    public void Parse_RejectsInvalidReusableWorkflowCallerJobShape()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              build:
                uses: ./.workflows/reusable-build.yml
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.build.runs-on cannot be used when the job calls a reusable workflow with uses.");
        Assert.Contains(result.Errors, error => error == "workflow.jobs.build.steps cannot be used when the job calls a reusable workflow with uses.");
    }

    [Fact]
    public void Parse_RejectsRemoteReusableWorkflowCallerJob()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              build:
                uses: owner/repo/.github/workflows/build.yml@v1
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.build.uses supports only local reusable workflow references in this milestone.");
    }

    [Fact]
    public void Parse_RejectsReusableWorkflowSecretInheritance()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              build:
                uses: ./.workflows/reusable-build.yml
                secrets: inherit
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "workflow.jobs.build.secrets: inherit is not supported for local reusable workflow calls.");
    }

    [Fact]
    public void Parse_AcceptsDeclaredWorkflowCallSecretsInConditions()
    {
        var result = Parse(
            """
            name: Reusable Build
            on:
              workflow_call:
                secrets:
                  token:
                    required: true
            jobs:
              build:
                if: "${{ secrets.token != '' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Build
                    run: dotnet build
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
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
                    with:
                      entrypoint: /bin/echo
                      args: '"hello world" --count 2'
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("mutable Docker image reference", StringComparison.OrdinalIgnoreCase));
        var step = result.Workflow!.Jobs["test"].Steps[0];
        Assert.Equal("docker://node:22", step.Uses);
        Assert.Equal("/bin/echo", step.With["entrypoint"]);
        Assert.Equal("\"hello world\" --count 2", step.With["args"]);
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
                if: "${{ unknownFunction() }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unsupported expression", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_AcceptsJobStatusAndHelperFunctions()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              prepare:
                runs-on: ubuntu-latest
                steps:
                  - name: Prepare
                    run: dotnet restore
              test:
                needs: prepare
                if: ${{ always() && contains(fromJSON('["push","workflow_dispatch"]'), github.event.event_name) && hashFiles('**/*.cs') != '' }}
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Parse_AcceptsStepConditionExpressions()
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
              prepare:
                runs-on: ubuntu-latest
                outputs:
                  changed: "true"
                steps:
                  - name: Prepare
                    run: dotnet restore
              test:
                needs: prepare
                runs-on: ubuntu-latest
                steps:
                  - name: Input condition
                    if: "${{ inputs.environment == 'staging' }}"
                    run: dotnet test
                  - name: Needs condition
                    if: "${{ needs.prepare.outputs.changed == 'true' }}"
                    run: dotnet test
                  - name: Failure condition
                    if: "${{ failure() }}"
                    run: echo failed
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Parse_AcceptsBooleanAndComparisonConditionExpressions()
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
              prepare:
                runs-on: ubuntu-latest
                outputs:
                  changed-count: "2"
                steps:
                  - name: Prepare
                    run: dotnet restore
              test:
                needs: prepare
                if: "${{ inputs.environment == 'staging' && needs.prepare.outputs.changed-count >= 2 && github.event.event_name != 'schedule' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    if: "${{ success() && inputs.environment == 'staging' }}"
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Parse_AcceptsCoreContextReferences()
    {
        var result = Parse(
            """
            name: CI
            env:
              RUN_TESTS: "true"
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
                if: "${{ env.RUN_TESTS == 'true' && github.workflow == 'CI' && github.run_id != '' && github.actor != '' && github.triggering_actor != '' && runner.os == 'Linux' && needs.prepare.result == 'success' }}"
                runs-on: ubuntu-latest
                steps:
                  - id: detect
                    name: Detect
                    run: echo "actio.output changed=true"
                  - name: Use context
                    if: "${{ job.status == 'running' && step.name == 'Use context' && steps.detect.outputs.changed == 'true' && steps.detect.outcome == 'success' && env.STEP_FLAG == 'true' }}"
                    env:
                      STEP_FLAG: "true"
                    run: dotnet test
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
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
    public void Parse_RejectsUnsupportedConditionContext()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                if: "${{ github.sha == 'abc123' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unsupported expression context 'github.sha'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsUnavailableConditionContext()
    {
        var result = Parse(
            """
            name: CI
            jobs:
              test:
                if: "${{ secrets.TOKEN != '' }}"
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("secrets", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("no workflow_call secret named 'TOKEN'", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void Parse_RejectsStepConditionReferenceNotDeclaredInNeeds()
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
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    if: "${{ needs.prepare.outputs.changed == 'true' }}"
                    run: dotnet test
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("workflow.jobs.test.steps[0].if", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("not declared", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowParseResult Parse(string yaml)
    {
        return new WorkflowParser().Parse(new StringReader(yaml));
    }
}
