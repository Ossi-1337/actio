using Actio.Core.Patterns;

namespace Actio.Core.Tests;

public sealed class WorkflowPatternMatcherTests
{
    [Fact]
    public void Matches_SupportsSegmentWildcards()
    {
        var options = WorkflowPatternOptions.CaseSensitive;

        Assert.True(WorkflowPatternMatcher.Matches("src/*.cs", "src/Program.cs", options));
        Assert.False(WorkflowPatternMatcher.Matches("src/*.cs", "src/app/Program.cs", options));
        Assert.True(WorkflowPatternMatcher.Matches("src/file?.txt", "src/file1.txt", options));
        Assert.False(WorkflowPatternMatcher.Matches("src/file?.txt", "src/file10.txt", options));
        Assert.True(WorkflowPatternMatcher.Matches("src/+", "src/app", options));
        Assert.False(WorkflowPatternMatcher.Matches("src/+", "src/app/file.txt", options));
    }

    [Fact]
    public void Matches_SupportsRecursiveDirectorySegments()
    {
        var options = WorkflowPatternOptions.CaseSensitive;

        Assert.True(WorkflowPatternMatcher.Matches("**/*.cs", "Program.cs", options));
        Assert.True(WorkflowPatternMatcher.Matches("**/*.cs", "src/app/Program.cs", options));
        Assert.True(WorkflowPatternMatcher.Matches("src/**", "src", options));
        Assert.True(WorkflowPatternMatcher.Matches("src/**", "src/app/Program.cs", options));
        Assert.False(WorkflowPatternMatcher.Matches("src/**", "tests/app.cs", options));
    }

    [Fact]
    public void Matches_NormalizesWindowsSeparators()
    {
        var options = WorkflowPatternOptions.CaseSensitive;

        Assert.True(WorkflowPatternMatcher.Matches(@"src\**\Program.cs", "src/app/Program.cs", options));
        Assert.True(WorkflowPatternMatcher.Matches("src/**/Program.cs", @"src\app\Program.cs", options));
    }

    [Fact]
    public void MatchesOrdered_AppliesIncludeExcludeAndReIncludeInOrder()
    {
        var patterns = new[]
        {
            "releases/**",
            "!releases/**-alpha",
            "releases/special-alpha"
        };

        Assert.True(WorkflowPatternMatcher.MatchesOrdered(patterns, "releases/1.0"));
        Assert.False(WorkflowPatternMatcher.MatchesOrdered(patterns, "releases/1.0-alpha"));
        Assert.True(WorkflowPatternMatcher.MatchesOrdered(patterns, "releases/special-alpha"));
    }

    [Fact]
    public void Matches_SupportsBranchAndTagRefsWithSlashes()
    {
        var options = WorkflowPatternOptions.CaseSensitive;

        Assert.True(WorkflowPatternMatcher.Matches("feature/+", "feature/login", options));
        Assert.False(WorkflowPatternMatcher.Matches("feature/+", "feature/team/login", options));
        Assert.True(WorkflowPatternMatcher.Matches("releases/**", "releases/2026/07", options));
        Assert.True(WorkflowPatternMatcher.Matches("v+", "v4", options));
    }

    [Fact]
    public void Matches_CanUseExplicitCaseInsensitiveMatching()
    {
        Assert.True(WorkflowPatternMatcher.Matches(
            "SRC/*.CS",
            "src/program.cs",
            WorkflowPatternOptions.CaseInsensitive));
    }
}
