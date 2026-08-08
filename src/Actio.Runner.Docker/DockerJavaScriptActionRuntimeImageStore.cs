using Actio.Engine.Execution;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Actio.Runner.Docker;

internal sealed class DockerJavaScriptActionRuntimeImageStore : IJavaScriptActionRuntimeImageStore
{
    public async Task<JavaScriptActionRuntimeImageInspection> InspectAsync(
        string image,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(CreateInspectStartInfo(image));
        var result = await RunBufferedAsync(process, cancellationToken);
        if (!result.Success)
        {
            return new JavaScriptActionRuntimeImageInspection(false);
        }

        try
        {
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(result.StandardOutput);
            return new JavaScriptActionRuntimeImageInspection(true, labels);
        }
        catch (JsonException)
        {
            return new JavaScriptActionRuntimeImageInspection(true);
        }
    }

    public async Task<JavaScriptActionRuntimeImageBuildResult> BuildAsync(
        JavaScriptActionRuntimeDescriptor runtime,
        string dockerfile,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(CreateBuildStartInfo(runtime));
        try
        {
            if (!process.Start())
            {
                return new(false, "Docker build process could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            return new(false, $"Docker could not be started: {ex.Message}");
        }

        try
        {
            await process.StandardInput.WriteAsync(dockerfile.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            var outputTask = RedirectLinesAsync(process.StandardOutput, output.WriteOutputLineAsync, cancellationToken);
            var errorTask = RedirectLinesAsync(process.StandardError, output.WriteOutputLineAsync, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }
        catch (IOException ex)
        {
            TryKillProcess(process);
            return new(false, $"Docker build I/O failed: {ex.Message}");
        }

        return process.ExitCode == 0
            ? new(true)
            : new(false, $"Docker build exited with code {process.ExitCode}.");
    }

    internal static ProcessStartInfo CreateBuildStartInfo(JavaScriptActionRuntimeDescriptor runtime)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.RedirectStandardInput = true;
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("--tag");
        startInfo.ArgumentList.Add(runtime.Image);
        AddBuildArgument(startInfo, "BASE_IMAGE", runtime.BaseImage);
        AddBuildArgument(startInfo, "GIT_VERSION", runtime.GitVersion);
        AddBuildArgument(startInfo, "CA_CERTIFICATES_VERSION", runtime.CaCertificatesVersion);
        foreach (var label in runtime.ExpectedLabels)
        {
            startInfo.ArgumentList.Add("--label");
            startInfo.ArgumentList.Add($"{label.Key}={label.Value}");
        }

        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    internal static ProcessStartInfo CreateInspectStartInfo(string image)
    {
        var startInfo = CreateDockerStartInfo();
        startInfo.ArgumentList.Add("image");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{json .Config.Labels}}");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(image);
        return startInfo;
    }

    private static void AddBuildArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("--build-arg");
        startInfo.ArgumentList.Add($"{name}={value}");
    }

    private static Process CreateProcess(ProcessStartInfo startInfo)
    {
        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    private static ProcessStartInfo CreateDockerStartInfo()
    {
        return new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static async Task<DockerCommandResult> RunBufferedAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!process.Start())
            {
                return new(1, string.Empty, "Docker process could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            return new(1, string.Empty, $"Docker could not be started: {ex.Message}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }

        return new(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task RedirectLinesAsync(
        TextReader reader,
        Func<string, CancellationToken, Task> writeLine,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await writeLine(line, cancellationToken);
        }
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
        catch (SystemException)
        {
        }
    }

    private sealed record DockerCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public bool Success => ExitCode == 0;
    }
}
