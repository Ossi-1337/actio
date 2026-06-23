using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        builder.Services.AddSingleton<ActioWebDataService>();

        var app = builder.Build();
        MapRoutes(app);
        return app;
    }

    public async Task RunAsync(ActioWebOptions options, CancellationToken cancellationToken = default)
    {
        await Build(options).RunAsync(cancellationToken);
    }

    private static void MapRoutes(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(EmbeddedWebAssetLoader.ReadText("index.html"), "text/html"));
        app.MapGet("/runs/{runId}", () => Results.Content(EmbeddedWebAssetLoader.ReadText("index.html"), "text/html"));
        app.MapGet("/assets/styles.css", () => Results.Content(EmbeddedWebAssetLoader.ReadText("styles.css"), "text/css"));
        app.MapGet("/assets/app.js", () => Results.Content(EmbeddedWebAssetLoader.ReadText("app.js"), "application/javascript"));

        app.MapGet("/api/health", (ActioWebOptions options) => Results.Ok(new
        {
            status = "ok",
            projectRoot = options.ProjectRoot,
            actioHome = options.ActioHome
        }));

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

}
