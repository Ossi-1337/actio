using System.Text.Json.Nodes;
using Actio.Core.Expressions;

namespace Actio.Core.Tests;

public sealed class ExpressionContextDataTests
{
    [Fact]
    public void Resolve_ReturnsNestedContextValues()
    {
        var context = new ExpressionContextData(
            [
                ExpressionContextRoot.AvailableRoot(
                    "github",
                    new JsonObject
                    {
                        ["event_name"] = "workflow_dispatch",
                        ["event"] = new JsonObject
                        {
                            ["source"] = "CLI"
                        }
                    })
            ]);

        var result = context.Resolve(new ExpressionReference("github", ["event", "source"]));

        Assert.True(result.Success, result.Error);
        Assert.Equal("CLI", result.Value.AsString());
    }

    [Fact]
    public void Resolve_FailsPredictablyForUnavailableContext()
    {
        var context = new ExpressionContextData(
            [
                ExpressionContextRoot.UnavailableRoot("secrets", "Expression context 'secrets' is not available.")
            ]);

        var result = context.Resolve(new ExpressionReference("secrets", ["TOKEN"]));

        Assert.False(result.Success);
        Assert.Equal("Expression context 'secrets' is not available.", result.Error);
    }

    [Fact]
    public void Resolve_ReturnsNullForMissingDynamicProperties()
    {
        var context = new ExpressionContextData(
            [
                ExpressionContextRoot.AvailableRoot(
                    "env",
                    ExpressionContextData.FromStrings(new Dictionary<string, string>()),
                    allowMissingProperties: true)
            ]);

        var result = context.Resolve(new ExpressionReference("env", ["MISSING"]));

        Assert.True(result.Success, result.Error);
        Assert.Equal(ExpressionValueKind.Null, result.Value.Kind);
    }

    [Fact]
    public void ToSafeJson_ExcludesUserProvidedAndUnavailableRoots()
    {
        var context = new ExpressionContextData(
            [
                ExpressionContextRoot.AvailableRoot("runner", new JsonObject { ["os"] = "Linux" }),
                ExpressionContextRoot.AvailableRoot("env", new JsonObject { ["TOKEN"] = "secret" }, includeInSafeSnapshot: false),
                ExpressionContextRoot.UnavailableRoot("secrets", "Secrets are unavailable.")
            ]);

        var snapshot = context.ToSafeJson();

        Assert.True(snapshot.ContainsKey("runner"));
        Assert.False(snapshot.ContainsKey("env"));
        Assert.False(snapshot.ContainsKey("secrets"));
    }
}
