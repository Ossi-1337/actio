using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class FileSystemLocalValueProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"actio-local-values-{Guid.NewGuid():N}");

    public FileSystemLocalValueProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Load_ReadsVarsAndSecretsFromActioFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".actio"));
        File.WriteAllText(
            Path.Combine(_root, ".actio", "vars.env"),
            """
            BUILD_CONFIGURATION=Release
            quoted="yes"
            """);
        File.WriteAllText(
            Path.Combine(_root, ".actio", "secrets.env"),
            """
            NUGET_TOKEN=from-file
            """);

        var result = new FileSystemLocalValueProvider(() => new Dictionary<string, string>()).Load(_root);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("Release", result.Values.Variables["BUILD_CONFIGURATION"]);
        Assert.Equal("yes", result.Values.Variables["quoted"]);
        Assert.Equal("from-file", result.Values.Secrets["NUGET_TOKEN"]);
    }

    [Fact]
    public void Load_EnvironmentValuesOverrideFileValues()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".actio"));
        File.WriteAllText(Path.Combine(_root, ".actio", "vars.env"), "BUILD_CONFIGURATION=Debug");
        File.WriteAllText(Path.Combine(_root, ".actio", "secrets.env"), "NUGET_TOKEN=from-file");
        var environment = new Dictionary<string, string>
        {
            ["ACTIO_VAR_BUILD_CONFIGURATION"] = "Release",
            ["ACTIO_SECRET_NUGET_TOKEN"] = "from-env"
        };

        var result = new FileSystemLocalValueProvider(() => environment).Load(_root);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("Release", result.Values.Variables["BUILD_CONFIGURATION"]);
        Assert.Equal("from-env", result.Values.Secrets["NUGET_TOKEN"]);
    }

    [Fact]
    public void Load_ReturnsErrorsForInvalidLocalValueFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".actio"));
        File.WriteAllText(
            Path.Combine(_root, ".actio", "vars.env"),
            """
            1BAD=value
            MISSING_EQUALS
            """);

        var result = new FileSystemLocalValueProvider(() => new Dictionary<string, string>()).Load(_root);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("invalid variable name '1BAD'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("must use NAME=value syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AllowsMissingLocalValueFiles()
    {
        var result = new FileSystemLocalValueProvider(() => new Dictionary<string, string>()).Load(_root);

        Assert.True(result.Success);
        Assert.Empty(result.Values.Variables);
        Assert.Empty(result.Values.Secrets);
    }

    [Theory]
    [InlineData("line-one\nline-two")]
    [InlineData("line-one\r\nline-two")]
    public void Load_RejectsMultilineEnvironmentSecretsWithoutEchoingValue(string secret)
    {
        var result = new FileSystemLocalValueProvider(() => new Dictionary<string, string>
        {
            ["ACTIO_SECRET_SIGNING_KEY"] = secret
        }).Load(_root);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("SIGNING_KEY", error);
        Assert.DoesNotContain(secret, error);
        Assert.DoesNotContain("line-one", error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
