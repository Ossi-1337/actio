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
        IStepOutputSink output,
        CancellationToken cancellationToken = default)
    {
        if (!_imageResolver.TryResolveImage(request.RunsOn, out var image))
        {
            var message = $"Runner '{request.RunsOn}' is not mapped to a Docker image.";
            await output.WriteErrorLineAsync(message, cancellationToken);
            return new StepExecutionResult(1);
        }

        var containerName = CreateContainerName(request);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(request, image, containerName),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                const string message = "Docker process could not be started.";
                await output.WriteErrorLineAsync(message, cancellationToken);
                return new StepExecutionResult(1);
            }
        }
        catch (Win32Exception ex)
        {
            var message = $"Docker could not be started: {ex.Message}";
            await output.WriteErrorLineAsync(message, cancellationToken);
            return new StepExecutionResult(1);
        }

        var outputTask = RedirectOutputLinesAsync(process.StandardOutput, output, cancellationToken);
        var errorTask = RedirectErrorLinesAsync(process.StandardError, output, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            TryRemoveContainer(containerName);
            throw;
        }

        return new StepExecutionResult(process.ExitCode);
    }

    private static ProcessStartInfo CreateStartInfo(StepExecutionRequest request, string image, string containerName)
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
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add("actio=true");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.job={request.JobName}");
        startInfo.ArgumentList.Add("--label");
        startInfo.ArgumentList.Add($"actio.step={request.StepName}");
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

    private static async Task RedirectOutputLinesAsync(
        TextReader reader,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await output.WriteOutputLineAsync(line, cancellationToken);
        }
    }

    private static async Task RedirectErrorLinesAsync(
        TextReader reader,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await output.WriteErrorLineAsync(line, cancellationToken);
        }
    }

    private static string CreateContainerName(StepExecutionRequest request)
    {
        var name = $"actio-{SanitizeName(request.JobName)}-{SanitizeName(request.StepName)}-{Guid.NewGuid():N}";
        return name.Length <= 63 ? name : name[..63].TrimEnd('-');
    }

    private static string SanitizeName(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        var sanitized = new string(chars).Trim('-');

        return string.IsNullOrWhiteSpace(sanitized) ? "step" : sanitized;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void TryRemoveContainer(string containerName)
    {
        try
        {
            using var cleanup = Process.Start(new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "rm",
                    "-f",
                    containerName
                }
            });

            cleanup?.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
