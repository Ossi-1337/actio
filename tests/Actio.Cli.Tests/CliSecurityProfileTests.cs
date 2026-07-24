using Actio.Engine.Execution;

namespace Actio.Cli.Tests;

public sealed class CliSecurityProfileTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_RunFormsAcceptStrictProfile(bool officialCommand)
    {
        var args = officialCommand
            ? new[] { "run", "ci.yml", "--security-profile", "strict" }
            : ["ci.yml", "--security-profile", "strict"];
        var command = new CliParser().Parse(args);

        Assert.Equal(CliCommandKind.RunWorkflow, command.Kind);
        Assert.Equal(RunnerSecurityProfiles.Strict, command.SecurityProfile);
    }

    [Fact]
    public void Parse_RunDefaultsToSecureBaseline()
    {
        var command = new CliParser().Parse(["run", "ci.yml"]);

        Assert.Equal(RunnerSecurityProfiles.SecureBaseline, command.SecurityProfile);
    }

    [Theory]
    [InlineData("unsafe")]
    [InlineData("STRICT")]
    public void Parse_RejectsUnknownOrWrongCaseProfile(string value)
    {
        var command = new CliParser().Parse(
            ["run", "ci.yml", "--security-profile", value]);

        Assert.Equal(CliCommandKind.UsageError, command.Kind);
        Assert.Contains("secure-baseline", command.ErrorMessage);
        Assert.Contains("strict", command.ErrorMessage);
    }
}
