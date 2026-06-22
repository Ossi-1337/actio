using System.ComponentModel;
using System.Diagnostics;
using Actio.Engine.Execution;

namespace Actio.Runner.Docker;

public sealed class DockerRunnerProvider : IRunnerProvider
{
    private readonly DockerImageResolver _imageResolver;

    public DockerRunnerProvider()
        : this(new DockerImageResolver())
    {
    }

    public DockerRunnerProvider(DockerImageResolver imageResolver)
    {
        _imageResolver = imageResolver;
    }

    public bool SupportsRunner(string runsOn)
    {
        return _imageResolver.TryResolveImage(runsOn, out _);
    }

    public async Task<StepExecutionResult> ExecuteStepAsync(
        StepExecutionRequest request,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!_imageResolver.TryResolveImage(request.RunsOn, out var image))
        {
            error.WriteLine($"Runner '{request.RunsOn}' is not mapped to a Docker image.");
            return new StepExecutionResult(1);
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request, image),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                error.WriteLine("Docker process could not be started.");
                return new StepExecutionResult(1);
            }
        }
        catch (Win32Exception ex)
        {
            error.WriteLine($"Docker could not be started: {ex.Message}");
            return new StepExecutionResult(1);
        }

        var outputTask = RedirectLinesAsync(process.StandardOutput, output, cancellationToken);
        var errorTask = RedirectLinesAsync(process.StandardError, error, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);

        return new StepExecutionResult(process.ExitCode);
    }

    private static ProcessStartInfo CreateStartInfo(StepExecutionRequest request, string image)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--rm");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add($"{Path.GetFullPath(request.ProjectRoot)}:/workspace");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("/workspace");

        foreach (var (key, value) in request.Environment.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"{key}={value}");
        }

        startInfo.ArgumentList.Add(image);
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(request.Command);

        return startInfo;
    }

    private static async Task RedirectLinesAsync(
        TextReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            writer.WriteLine(line);
        }
    }
}
