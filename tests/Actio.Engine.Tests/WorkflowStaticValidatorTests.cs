using Actio.Engine.Validation;

namespace Actio.Engine.Tests;

public sealed class WorkflowStaticValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-static-validation-{Guid.NewGuid():N}");

    public WorkflowStaticValidatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".workflows"));
    }

    [Fact]
    public void Validate_DetectsJobDependencyCycle()
    {
        var path = WriteWorkflow(
            """
            name: CI
            jobs:
              first:
                needs: second
                runs-on: ubuntu-latest
                steps:
                  - name: First
                    run: echo first
              second:
                needs: first
                runs-on: ubuntu-latest
                steps:
                  - name: Second
                    run: echo second
            """);

        var result = Validate(path);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Message.Contains("circular needs dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InspectsLocalActionInputsAndEntrypoints()
    {
        var actionRoot = Path.Combine(_root, ".actio", "actions", "sample");
        Directory.CreateDirectory(actionRoot);
        File.WriteAllText(
            Path.Combine(actionRoot, "action.yml"),
            """
            name: Sample
            inputs:
              message:
                required: true
            runs:
              using: node24
              main: dist/index.js
            """);
        var path = WriteWorkflow(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Sample
                    uses: ./.actio/actions/sample
            """);

        var result = Validate(path);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Message.Contains("Required action input 'message' is missing", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Message.Contains("entrypoint 'dist/index.js' is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RecursesIntoLocalReusableWorkflow()
    {
        File.WriteAllText(
            Path.Combine(_root, ".workflows", "called.yml"),
            """
            name: Called
            on:
              workflow_call:
                inputs:
                  target:
                    required: true
                    type: string
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: Test
                    run: echo test
            """);
        var path = WriteWorkflow(
            """
            name: CI
            jobs:
              called:
                uses: ./.workflows/called.yml
            """);

        var result = Validate(path);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Message.Contains("missing required reusable workflow input 'target'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsUnknownExternalActionWithWarning()
    {
        var path = WriteWorkflow(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: External
                    uses: owner/repository@v1
            """);

        var result = Validate(path);

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("metadata was not inspected", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsKnownUnsupportedAction()
    {
        var path = WriteWorkflow(
            """
            name: CI
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - name: GitHub script
                    uses: actions/github-script@v7
            """);

        var result = Validate(path);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Message.Contains("listed in Actio's compatibility matrix as Unsupported", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WorkflowStaticValidationResult Validate(string workflowPath)
    {
        return new WorkflowStaticValidator().Validate(
            workflowPath,
            _root,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }

    private string WriteWorkflow(string content)
    {
        var path = Path.Combine(_root, ".workflows", "ci.yml");
        File.WriteAllText(path, content);
        return path;
    }
}
