using Actio.Git;

namespace Actio.Git.Tests;

public sealed class GitPrePushInputParserTests
{
    [Fact]
    public void Parse_ReadsBranchAndTagUpdates()
    {
        var before = new string('1', 40);
        var after = new string('2', 40);
        var result = GitPrePushInputParser.Parse(
            $"refs/heads/dev {after} refs/heads/main {before}\n" +
            $"refs/tags/v1 {after} refs/tags/v1 {new string('0', 40)}\n");

        Assert.True(result.Success);
        Assert.Collection(
            result.Updates,
            update =>
            {
                Assert.Equal(GitReferenceKind.Branch, update.ReferenceKind);
                Assert.Equal("main", update.ReferenceName);
            },
            update =>
            {
                Assert.Equal(GitReferenceKind.Tag, update.ReferenceKind);
                Assert.True(update.IsNewRef);
            });
    }

    [Fact]
    public void Parse_RecognizesDeletionAndUnsupportedReference()
    {
        var objectId = new string('1', 40);
        var result = GitPrePushInputParser.Parse(
            $"(delete) {new string('0', 40)} refs/remotes/origin/main {objectId}");

        var update = Assert.Single(result.Updates);
        Assert.True(update.IsDeletion);
        Assert.Equal(GitReferenceKind.Unsupported, update.ReferenceKind);
    }

    [Theory]
    [InlineData("not enough fields")]
    [InlineData("refs/heads/main bad refs/heads/main also-bad")]
    public void Parse_RejectsMalformedInput(string input)
    {
        var result = GitPrePushInputParser.Parse(input);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }
}
