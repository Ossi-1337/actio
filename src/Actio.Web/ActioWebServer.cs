using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Actio.Web.Models;

namespace Actio.Web;

public sealed class ActioWebServer
{
    public WebApplication Build(ActioWebOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.UseUrls(options.Url);
        if (options.Background)
        {
            builder.Logging.ClearProviders();
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ActioWebRuntimeState(options.Url));
        builder.Services.AddSingleton<ActioWebDataService>();

        var app = builder.Build();
        MapRoutes(app, options);
        return app;
    }

    public async Task RunAsync(ActioWebOptions options, CancellationToken cancellationToken = default)
    {
        await RunAsync(options, onStarted: null, cancellationToken);
    }

    public async Task RunAsync(
        ActioWebOptions options,
        Func<ActioWebServerBinding, CancellationToken, Task>? onStarted,
        CancellationToken cancellationToken = default)
    {
        await using var app = Build(options);
        await app.StartAsync(cancellationToken);

        var serverUrl = ResolveServerUrl(app);
        app.Services.GetRequiredService<ActioWebRuntimeState>().ServerUrl = serverUrl;
        if (onStarted is not null)
        {
            await onStarted(new ActioWebServerBinding(serverUrl), cancellationToken);
        }

        await app.WaitForShutdownAsync(cancellationToken);
    }

    private static void MapRoutes(WebApplication app, ActioWebOptions options)
    {
        app.MapGet("/", () => Results.Content(EmbeddedWebAssetLoader.ReadText("index.html"), "text/html"));
        app.MapGet("/runs/{runId}", () => Results.Content(EmbeddedWebAssetLoader.ReadText("index.html"), "text/html"));
        app.MapGet("/settings", () => Results.Content(EmbeddedWebAssetLoader.ReadText("index.html"), "text/html"));
        app.MapGet("/assets/styles.css", () => Results.Content(EmbeddedWebAssetLoader.ReadText("styles.css"), "text/css"));
        app.MapGet("/assets/app.js", () => Results.Content(EmbeddedWebAssetLoader.ReadText("app.js"), "application/javascript"));

        app.MapGet("/api/health", (
            ActioWebDataService data,
            ActioWebRuntimeState runtime) => Results.Ok(new
            {
                status = "ok",
                projectRoot = data.ProjectRoot,
                actioHome = data.ActioHome,
                serverUrl = runtime.ServerUrl,
                cacheRoot = data.CacheRoot,
                runtimeIdentity = options.RuntimeIdentity,
                webInstanceId = options.WebInstanceId,
                processId = options.ProcessId,
                processStartTimeUtcTicks = options.ProcessStartTimeUtcTicks,
                sessionId = options.SessionId
            }));

        if (options.Background && options.ControlToken is not null)
        {
            app.MapPost("/api/internal/shutdown", (
                HttpContext context,
                IHostApplicationLifetime lifetime) =>
            {
                if (!IsAuthorizedShutdown(context, options.ControlToken))
                {
                    return Results.NotFound();
                }

                lifetime.StopApplication();
                return Results.Accepted();
            });
        }

        app.MapGet("/api/workflows", async (ActioWebDataService data, CancellationToken cancellationToken) =>
            Results.Ok(await data.GetWorkflowsAsync(cancellationToken)));

        app.MapGet("/api/runs", async (ActioWebDataService data, CancellationToken cancellationToken) =>
            Results.Ok(await data.GetRunsAsync(cancellationToken)));

        app.MapGet("/api/runs/{runId}", async (string runId, ActioWebDataService data, CancellationToken cancellationToken) =>
        {
            var run = await data.GetRunAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        app.MapGet("/api/runs/{runId}/workflow-file", async (string runId, ActioWebDataService data, CancellationToken cancellationToken) =>
        {
            var content = await data.GetWorkflowFileAsync(runId, cancellationToken);
            return content is null ? Results.NotFound() : Results.Text(content, "text/yaml");
        });

        app.MapGet("/api/runs/{runId}/workflow-file/download", async (string runId, ActioWebDataService data, CancellationToken cancellationToken) =>
        {
            var workflowFile = await data.GetWorkflowFileResultAsync(runId, cancellationToken);
            return workflowFile is null
                ? Results.NotFound()
                : Results.File(
                    Encoding.UTF8.GetBytes(workflowFile.Content),
                    "text/yaml",
                    workflowFile.FileName,
                    enableRangeProcessing: false);
        });

        app.MapPut("/api/runs/{runId}/workflow-file", async (
            string runId,
            WorkflowFileUpdateRequest request,
            ActioWebDataService data,
            CancellationToken cancellationToken) =>
        {
            var result = await data.UpdateWorkflowFileAsync(runId, request.Content, cancellationToken);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapGet("/api/cache", async (ActioWebDataService data, CancellationToken cancellationToken) =>
            Results.Ok(await data.GetCacheAsync(cancellationToken)));

        app.MapDelete("/api/cache", async (ActioWebDataService data, CancellationToken cancellationToken) =>
            Results.Ok(await data.CleanCacheAsync(cancellationToken)));

        app.MapPost("/api/runs/{runId}/cancel", async (string runId, ActioWebDataService data, CancellationToken cancellationToken) =>
        {
            var result = await data.CancelRunAsync(runId, cancellationToken);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapPost("/api/runs/{runId}/rerun", async (string runId, ActioWebDataService data, CancellationToken cancellationToken) =>
        {
            var result = await data.RerunAsync(runId, cancellationToken);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapGet("/api/runs/{runId}/logs", async (
            string runId,
            string job,
            string step,
            ActioWebDataService data,
            CancellationToken cancellationToken) =>
        {
            var log = await data.GetStepLogAsync(runId, job, step, cancellationToken);
            return log is null ? Results.NotFound() : Results.Text(log.Content, "text/plain");
        });

        app.MapGet("/api/runs/{runId}/artifacts", async (
            string runId,
            string job,
            string name,
            ActioWebDataService data,
            CancellationToken cancellationToken) =>
        {
            var artifact = await data.GetArtifactAsync(runId, job, name, cancellationToken);
            if (artifact is null)
            {
                return Results.NotFound();
            }

            if (artifact.IsFile)
            {
                return Results.File(
                    artifact.Path,
                    artifact.ContentType ?? "application/octet-stream",
                    Path.GetFileName(artifact.Path),
                    enableRangeProcessing: true);
            }

            return Results.Ok(new
            {
                path = artifact.Path,
                entries = artifact.DirectoryEntries
            });
        });
    }

    internal static bool IsAuthorizedShutdown(HttpContext context, string expectedToken)
    {
        if (context.Connection.RemoteIpAddress is not { } remoteAddress ||
            !IPAddress.IsLoopback(remoteAddress) ||
            !context.Request.Headers.TryGetValue("X-Actio-Control-Token", out var providedValues))
        {
            return false;
        }

        var provided = providedValues.Count == 1 ? providedValues[0] : null;
        if (provided is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static string ResolveServerUrl(WebApplication app)
    {
        var urls = app.Urls
            .Select(url => url.TrimEnd('/'))
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.IsLoopback &&
                uri.Port > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return urls.Length == 1
            ? urls[0]
            : throw new InvalidOperationException(
                $"Actio web expected one bound loopback URL, but found {urls.Length}.");
    }

    private sealed class ActioWebRuntimeState(string serverUrl)
    {
        public string ServerUrl { get; set; } = serverUrl;
    }
}

public sealed record ActioWebServerBinding(string ServerUrl);
