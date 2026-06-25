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
        var step = Assert.Single(result.Action!.Steps);
        Assert.Equal("Greet", step.Name);
        Assert.Equal("echo hello", step.Run);
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
        Assert.Contains(result.Errors, error => error.Contains("supports only 'composite'", StringComparison.OrdinalIgnoreCase));
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
