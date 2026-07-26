using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Actio.Web.Tests;

public sealed class WebControlPlaneSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:17345", false, true)]
    [InlineData("http://127.99.1.2:17345", false, true)]
    [InlineData("http://[::1]:17345", false, true)]
    [InlineData("http://127.0.0.1:0", true, true)]
    [InlineData("http://127.0.0.1:0", false, false)]
    [InlineData("https://127.0.0.1:17345", false, false)]
    [InlineData("http://0.0.0.0:17345", false, false)]
    [InlineData("http://192.168.1.20:17345", false, false)]
    [InlineData("http://localhost:17345", false, false)]
    [InlineData("http://127.0.0.1%2f.example:17345", false, false)]
    [InlineData("http://127.0.0.1:17345/api", false, false)]
    [InlineData("http://user@127.0.0.1:17345", false, false)]
    [InlineData("http://127.0.0.1:17345?x=1", false, false)]
    public void LoopbackPolicyAcceptsOnlyLiteralLocalHttpUrls(
        string value,
        bool background,
        bool expected)
    {
        var valid = LoopbackWebUrlPolicy.TryValidate(
            value,
            background,
            out _,
            out _);

        Assert.Equal(expected, valid);
    }

    [Fact]
    public async Task MutationPolicyAllowsNativeAndSameOriginRequests()
    {
        var native = CreateContext("http://127.0.0.1:17345");
        var nativeReached = false;
        await ActioWebServer.EnforceLocalMutationOriginAsync(
            native,
            _ =>
            {
                nativeReached = true;
                return Task.CompletedTask;
            });

        var browser = CreateContext("http://127.0.0.1:17345");
        browser.Request.Headers.Origin = "http://127.0.0.1:17345";
        var browserReached = false;
        await ActioWebServer.EnforceLocalMutationOriginAsync(
            browser,
            _ =>
            {
                browserReached = true;
                return Task.CompletedTask;
            });

        Assert.True(nativeReached);
        Assert.True(browserReached);
    }

    [Theory]
    [InlineData("http://127.0.0.1:17346", null)]
    [InlineData("http://example.test", null)]
    [InlineData(null, "cross-site")]
    public async Task MutationPolicyRejectsCrossOriginBrowserRequests(
        string? origin,
        string? fetchSite)
    {
        var context = CreateContext("http://127.0.0.1:17345");
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        var reached = false;
        await ActioWebServer.EnforceLocalMutationOriginAsync(
            context,
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(string serverUrl)
    {
        var services = new ServiceCollection()
            .AddSingleton(new ActioWebServer.ActioWebRuntimeState(serverUrl))
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Request.Method = HttpMethods.Post;
        return context;
    }
}
