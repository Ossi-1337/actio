using Actio.Core.Actions;

namespace Actio.Core.Tests;

public sealed class ActionParserTests
{
    [Fact]
    public void Parse_AcceptsCompositeAction()
    {
        var result = Parse(
            """
            name: Say hello
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo hello
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("Say hello", result.Action!.Name);
        var step = Assert.Single(result.Action.Steps);
        Assert.Equal("Greet", step.Name);
        Assert.Equal("echo hello", step.Run);
    }

    [Fact]
    public void Parse_AcceptsCompositeActionMetadataAndShell()
    {
        var result = Parse(
            """
            name: Say hello
            description: Greet from a composite action
            inputs:
              name:
                description: Name to greet
            outputs:
              greeting:
                description: Greeting output
                value: "${{ steps.greet.outputs.greeting }}"
            branding:
              icon: terminal
              color: green
            runs:
              using: composite
              steps:
                - id: greet
                  name: Greet
                  shell: bash
                  working-directory: tools
                  run: echo hello
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var input = result.Action!.Inputs["name"];
        Assert.Equal("Name to greet", input.Description);
        Assert.False(input.Required);
        var output = result.Action.Outputs["greeting"];
        Assert.Equal("Greeting output", output.Description);
        Assert.Equal("${{ steps.greet.outputs.greeting }}", output.Value);
        var step = Assert.Single(result.Action!.Steps);
        Assert.Equal("greet", step.Id);
        Assert.Equal("Greet", step.Name);
        Assert.Equal("echo hello", step.Run);
        Assert.Equal("bash", step.Shell);
        Assert.Equal("tools", step.WorkingDirectory);
    }

    [Fact]
    public void Parse_AllowsCompositeActionStepNameToBeOmittedWhenIdExists()
    {
        var result = Parse(
            """
            name: Say hello
            runs:
              using: composite
              steps:
                - id: greet
                  run: echo hello
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var step = Assert.Single(result.Action!.Steps);
        Assert.Equal("greet", step.Id);
        Assert.Equal("greet", step.Name);
    }

    [Fact]
    public void Parse_AcceptsActionInputDefaultsAndRequiredFlags()
    {
        var result = Parse(
            """
            name: Say hello
            inputs:
              name:
                description: Name to greet
                required: true
              punctuation:
                default: "!"
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo hello
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.True(result.Action!.Inputs["name"].Required);
        Assert.Null(result.Action.Inputs["name"].Default);
        Assert.False(result.Action.Inputs["punctuation"].Required);
        Assert.Equal("!", result.Action.Inputs["punctuation"].Default);
    }

    [Fact]
    public void Parse_AcceptsNode20JavaScriptAction()
    {
        var result = Parse(
            """
            name: JavaScript hello
            inputs:
              name:
                required: true
            outputs:
              cache-hit:
                description: Whether a cache was restored
            runs:
              using: node20
              pre: dist/pre.js
              main: dist/index.js
              post: dist/post.js
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("JavaScript hello", result.Action!.Name);
        Assert.Equal(ActionRuntime.Node20, result.Action.Runtime);
        Assert.Empty(result.Action.Steps);
        Assert.Equal("dist/pre.js", result.Action.Pre);
        Assert.Equal("dist/index.js", result.Action.Main);
        Assert.Equal("dist/post.js", result.Action.Post);
        Assert.True(result.Action.Inputs["name"].Required);
        Assert.Null(result.Action.Outputs["cache-hit"].Value);
    }

    [Fact]
    public void Parse_AcceptsNode24JavaScriptAction()
    {
        var result = Parse(
            """
            name: JavaScript hello
            runs:
              using: node24
              pre: dist/pre.js
              main: dist/index.js
              post: dist/post.js
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(ActionRuntime.Node24, result.Action!.Runtime);
        Assert.Equal("dist/pre.js", result.Action.Pre);
        Assert.Equal("dist/index.js", result.Action.Main);
        Assert.Equal("dist/post.js", result.Action.Post);
    }

    [Fact]
    public void Parse_AcceptsDockerfileAction()
    {
        var result = Parse(
            """
            name: Dockerfile hello
            inputs:
              name:
                default: Actio
            runs:
              using: docker
              image: Dockerfile
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("Dockerfile hello", result.Action!.Name);
        Assert.Equal(ActionRuntime.Docker, result.Action.Runtime);
        Assert.Equal("Dockerfile", result.Action.Image);
        Assert.Empty(result.Action.Steps);
        Assert.Equal("Actio", result.Action.Inputs["name"].Default);
    }

    [Fact]
    public void Parse_RejectsInvalidActionInputMetadata()
    {
        var result = Parse(
            """
            name: Say hello
            inputs:
              name:
                required: maybe
              bad:
                nested:
                  value: no
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo hello
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.inputs.name.required must be true or false.");
        Assert.Contains(result.Errors, error => error == "action.inputs.bad.nested is not supported.");
    }

    [Fact]
    public void Parse_RejectsInvalidActionOutputMetadata()
    {
        var result = Parse(
            """
            name: Say hello
            outputs:
              greeting:
                unknown: no
            runs:
              using: composite
              steps:
                - name: Greet
                  run: echo hello
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.outputs.greeting.unknown is not supported.");
        Assert.Contains(result.Errors, error => error == "action.outputs.greeting.value is required.");
    }

    [Fact]
    public void Parse_RejectsDockerActionSteps()
    {
        var result = Parse(
            """
            name: Docker action
            runs:
              using: docker
              steps:
                - name: Run
                  run: echo hello
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.runs.steps is supported only when action.runs.using is 'composite'.");
        Assert.Contains(result.Errors, error => error == "action.runs.image is required.");
    }

    [Fact]
    public void Parse_RejectsUnsupportedJavaScriptRuntime()
    {
        var result = Parse(
            """
            name: JavaScript action
            runs:
              using: node16
              main: dist/index.js
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains("supports only 'composite', 'node20', 'node24', or 'docker'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsUnsupportedDockerImageValue()
    {
        var result = Parse(
            """
            name: Docker action
            runs:
              using: docker
              image: docker://alpine:3.20
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.runs.image supports only 'Dockerfile' for Docker actions.");
    }

    [Fact]
    public void Parse_RejectsUnsupportedDockerActionEntrypointAndArgs()
    {
        var result = Parse(
            """
            name: Docker action
            runs:
              using: docker
              image: Dockerfile
              entrypoint: /entrypoint.sh
              args:
                - hello
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.runs.entrypoint is not supported.");
        Assert.Contains(result.Errors, error => error == "action.runs.args is not supported.");
    }

    [Theory]
    [InlineData("../dist/index.js")]
    [InlineData("/dist/index.js")]
    [InlineData("C:\\dist\\index.js")]
    [InlineData("dist/../index.js")]
    public void Parse_RejectsJavaScriptActionPathsOutsideActionDirectory(string main)
    {
        var result = Parse(
            $$"""
            name: JavaScript action
            runs:
              using: node20
              main: {{main}}
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("must be a relative path inside the action directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_RejectsJavaScriptActionLifecycleConditions()
    {
        var result = Parse(
            """
            name: JavaScript action
            runs:
              using: node20
              main: dist/index.js
              post: dist/post.js
              post-if: success()
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.runs.post-if is not supported.");
    }

    [Fact]
    public void Parse_AcceptsNestedUsesSteps()
    {
        var result = Parse(
            """
            name: Nested action
            runs:
              using: composite
              steps:
                - name: Other
                  uses: ./other
                  with:
                    name: Actio
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var step = Assert.Single(result.Action!.Steps);
        Assert.Equal("Other", step.Name);
        Assert.Null(step.Run);
        Assert.Equal("./other", step.Uses);
        Assert.Equal("Actio", step.With["name"]);
    }

    [Fact]
    public void Parse_RejectsCompositeActionStepWithRunAndUses()
    {
        var result = Parse(
            """
            name: Invalid nested action
            runs:
              using: composite
              steps:
                - name: Other
                  run: echo hello
                  uses: ./other
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error == "action.runs.steps[0] cannot define both run and uses.");
    }

    private static ActionParseResult Parse(string yaml)
    {
        return new ActionParser().Parse(new StringReader(yaml));
    }
}
