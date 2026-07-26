namespace Actio.Cli.Tests;

public sealed class CliWebUrlPolicyTests
{
    [Theory]
    [InlineData("http://0.0.0.0:17345")]
    [InlineData("http://localhost:17345")]
    [InlineData("https://127.0.0.1:17345")]
    [InlineData("http://127.0.0.1:0")]
    public void Parse_RejectsUnsafeForegroundWebUrls(string url)
    {
        var command = new CliParser().Parse(["web", "--url", url]);

        Assert.Equal(CliCommandKind.UsageError, command.Kind);
        Assert.NotNull(command.ErrorMessage);
    }

    [Fact]
    public void Parse_AllowsDynamicPortOnlyForManagedBackgroundWorker()
    {
        var command = new CliParser().Parse(
            ["web", "--background", "--url", "http://127.0.0.1:0"]);

        Assert.Equal(CliCommandKind.RunWeb, command.Kind);
        Assert.True(command.Background);
    }
}
