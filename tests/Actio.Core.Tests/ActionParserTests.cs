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
            branding:
              icon: terminal
              color: green
            runs:
              using: composite
              steps:
                - name: Greet
                  shell: bash
                  run: echo hello
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var input = result.Action!.Inputs["name"];
        Assert.Equal("Name to greet", input.Description);
        Assert.False(input.Required);
        var step = Assert.Single(result.Action!.Steps);
        Assert.Equal("Greet", step.Name);
        Assert.Equal("echo hello", step.Run);
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
    public void Parse_RejectsUnsupportedUsingValue()
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
        Assert.Contains(result.Errors, error => error.Contains("supports only 'composite' or 'node20'", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(result.Errors, error => error.Contains("supports only 'composite' or 'node20'", StringComparison.OrdinalIgnoreCase));
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
    public void Parse_RejectsNestedUsesSteps()
    {
        var result = Parse(
            """
            name: Nested action
            runs:
              using: composite
              steps:
                - name: Other
                  uses: ./other
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("uses is not supported", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("run is required", StringComparison.OrdinalIgnoreCase));
    }

    private static ActionParseResult Parse(string yaml)
    {
        return new ActionParser().Parse(new StringReader(yaml));
    }
}
