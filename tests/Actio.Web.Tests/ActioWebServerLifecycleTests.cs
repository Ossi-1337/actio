using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Actio.Web.Tests;

public sealed class ActioWebServerLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"actio-web-lifecycle-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task HealthRouteIncludesRuntimeAndProcessIdentity()
    {
        var options = CreateOptions(
            background: true,
            runtimeIdentity: "runtime",
            instanceId: "instance",
            processId: 123,
            processStartTicks: 456,
            controlToken: "token");
        await using var app = new ActioWebServer().Build(options);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/api/health", routePatterns);
        Assert.Contains("/api/internal/shutdown", routePatterns);
    }

    [Fact]
    public async Task ForegroundServerDoesNotExposeShutdownRoute()
    {
        await using var app = new ActioWebServer().Build(CreateOptions(background: false));

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.DoesNotContain("/api/internal/shutdown", routePatterns);
    }

    [Fact]
    public void ShutdownAuthorizationRequiresLoopbackAndExactToken()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Actio-Control-Token"] = "expected";

        Assert.True(ActioWebServer.IsAuthorizedShutdown(context, "expected"));

        context.Request.Headers["X-Actio-Control-Token"] = "wrong";
        Assert.False(ActioWebServer.IsAuthorizedShutdown(context, "expected"));

        context.Request.Headers["X-Actio-Control-Token"] = "expected";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.False(ActioWebServer.IsAuthorizedShutdown(context, "expected"));
    }

    [Fact]
    public async Task DynamicBindingPublishesActualUrlAndSessionIdentity()
    {
        var options = CreateOptions(
            background: true,
            runtimeIdentity: "runtime",
            instanceId: "instance",
            processId: Environment.ProcessId,
            processStartTicks: 456,
            controlToken: "token",
            sessionId: "session");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource<ActioWebServerBinding>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = new ActioWebServer().RunAsync(
            options,
            (binding, _) =>
            {
                started.TrySetResult(binding);
                return Task.CompletedTask;
            },
            cancellation.Token);

        var binding = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(0, new Uri(binding.ServerUrl).Port);
        using var http = new HttpClient();
        using var health = await http.GetFromJsonAsync<JsonDocument>(
            $"{binding.ServerUrl}/api/health",
            cancellation.Token);
        Assert.Equal(
            binding.ServerUrl,
            health!.RootElement.GetProperty("serverUrl").GetString());
        Assert.Equal(
            "session",
            health.RootElement.GetProperty("sessionId").GetString());

        cancellation.Cancel();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ActioWebOptions CreateOptions(
        bool background,
        string? runtimeIdentity = null,
        string? instanceId = null,
        int? processId = null,
        long? processStartTicks = null,
        string? controlToken = null,
        string? sessionId = null)
    {
        var projectRoot = Path.Combine(_root, "project");
        var actioHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(actioHome);
        return new ActioWebOptions(
            projectRoot,
            actioHome,
            "http://127.0.0.1:0",
            background,
            runtimeIdentity,
            instanceId,
            processId,
            processStartTicks,
            controlToken,
            sessionId);
    }
}
