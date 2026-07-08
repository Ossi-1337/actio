using Actio.Core.Actions;

namespace Actio.Core.Tests;

public sealed class KnownActionCompatibilityCatalogTests
{
    [Theory]
    [InlineData("actions/checkout@v4", ActionCompatibilityStatus.Partial, "14")]
    [InlineData("actions/cache@v4", ActionCompatibilityStatus.Partial, "49")]
    [InlineData("actions/upload-artifact@v4", ActionCompatibilityStatus.Partial, "50")]
    [InlineData("actions/download-artifact@v4", ActionCompatibilityStatus.Partial, "50")]
    [InlineData("actions/setup-node@v4", ActionCompatibilityStatus.Partial, "51")]
    [InlineData("actions/setup-python@v5", ActionCompatibilityStatus.Partial, "51")]
    [InlineData("actions/setup-java@v4", ActionCompatibilityStatus.Partial, "51")]
    [InlineData("actions/setup-go@v5", ActionCompatibilityStatus.Partial, "51")]
    [InlineData("actions/setup-dotnet@v4", ActionCompatibilityStatus.Partial, "51")]
    [InlineData("actions/github-script@v7", ActionCompatibilityStatus.Unsupported, "52")]
    [InlineData("dorny/paths-filter@v3", ActionCompatibilityStatus.Unvalidated, "59")]
    public void Find_ReturnsKnownCompatibilityEntry(
        string uses,
        ActionCompatibilityStatus status,
        string milestone)
    {
        var entry = KnownActionCompatibilityCatalog.Find(uses);

        Assert.NotNull(entry);
        Assert.Equal(status, entry.Status);
        Assert.Contains(milestone, entry.RequiredMilestone);
        Assert.False(string.IsNullOrWhiteSpace(entry.Limitations));
        Assert.False(string.IsNullOrWhiteSpace(entry.Evidence));
    }

    [Fact]
    public void Find_DoesNotMatchUnknownActionPath()
    {
        var entry = KnownActionCompatibilityCatalog.Find("actions/checkout/path@v4");

        Assert.Null(entry);
    }

    [Fact]
    public void UnsupportedMessage_IncludesActionAndRequiredMilestone()
    {
        var entry = KnownActionCompatibilityCatalog.Find("actions/github-script@v7");

        var message = entry!.FormatUnsupportedMessage("actions/github-script@v7");

        Assert.Contains("actions/github-script@v7", message);
        Assert.Contains("Unsupported", message);
        Assert.Contains("GitHub API client context", message);
        Assert.Contains("GITHUB_TOKEN", message);
        Assert.Contains("52", message);
    }
}
