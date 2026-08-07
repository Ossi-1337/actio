using System.Diagnostics;
using System.Text;

namespace Actio.Git;

public enum GitHookState
{
    NotInstalled,
    Managed,
    Unmanaged
}

public sealed record GitHookResult(
    bool Success,
    GitHookState State,
    string Message,
    string? HookPath = null);

public interface IActioExecutableProbe
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

public sealed class ActioExecutableProbe : IActioExecutableProbe
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "actio",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
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
    }
}

public interface IGitHookManager
{
    Task<GitHookResult> InstallAsync(string workingDirectory, CancellationToken cancellationToken = default);

    Task<GitHookResult> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default);

    Task<GitHookResult> UninstallAsync(string workingDirectory, CancellationToken cancellationToken = default);
}

public sealed class GitHookManager : IGitHookManager
{
    public const string ManagedMarker = "# actio-managed-pre-push:v1";

    public const string HookContent = """
        #!/bin/sh
        # actio-managed-pre-push:v1
        exec actio hooks run pre-push "$@"
        """;

    private readonly IGitRepositoryClient _repository;
    private readonly IActioExecutableProbe _actioProbe;

    public GitHookManager(
        IGitRepositoryClient? repository = null,
        IActioExecutableProbe? actioProbe = null)
    {
        _repository = repository ?? new GitRepositoryClient();
        _actioProbe = actioProbe ?? new ActioExecutableProbe();
    }

    public async Task<GitHookResult> InstallAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(workingDirectory, cancellationToken);
        if (!context.Success)
        {
            return context.Result!;
        }

        if (!await _actioProbe.IsAvailableAsync(cancellationToken))
        {
            return Failed("The 'actio' command is not available on PATH. Install Actio before installing the hook.");
        }

        var hookPath = context.HookPath!;
        var state = ReadState(hookPath);
        if (state == GitHookState.Unmanaged)
        {
            return Failed($"An unmanaged pre-push hook already exists at '{hookPath}'. Actio did not overwrite it.", hookPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        var tempPath = $"{hookPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                HookContent.ReplaceLineEndings("\n") + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            SetExecutable(tempPath);
            File.Move(tempPath, hookPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return new GitHookResult(
            true,
            GitHookState.Managed,
            state == GitHookState.Managed
                ? "Actio pre-push hook is already installed and was refreshed."
                : "Actio pre-push hook installed.",
            hookPath);
    }

    public async Task<GitHookResult> GetStatusAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(workingDirectory, cancellationToken);
        if (!context.Success)
        {
            return context.Result!;
        }

        var hookPath = context.HookPath!;
        return ReadState(hookPath) switch
        {
            GitHookState.Managed => new GitHookResult(true, GitHookState.Managed, "Actio pre-push hook is installed.", hookPath),
            GitHookState.Unmanaged => new GitHookResult(true, GitHookState.Unmanaged, "A non-Actio pre-push hook is installed.", hookPath),
            _ => new GitHookResult(true, GitHookState.NotInstalled, "Actio pre-push hook is not installed.", hookPath)
        };
    }

    public async Task<GitHookResult> UninstallAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(workingDirectory, cancellationToken);
        if (!context.Success)
        {
            return context.Result!;
        }

        var hookPath = context.HookPath!;
        var state = ReadState(hookPath);
        if (state == GitHookState.Unmanaged)
        {
            return Failed($"The pre-push hook at '{hookPath}' is not managed by Actio and was not removed.", hookPath);
        }

        if (state == GitHookState.Managed)
        {
            File.Delete(hookPath);
            return new GitHookResult(true, GitHookState.NotInstalled, "Actio pre-push hook removed.", hookPath);
        }

        return new GitHookResult(true, GitHookState.NotInstalled, "Actio pre-push hook is not installed.", hookPath);
    }

    private async Task<HookContext> ResolveContextAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var inspection = await _repository.InspectAsync(workingDirectory, cancellationToken);
        if (!inspection.Success)
        {
            return HookContext.Failed(string.Join(" ", inspection.Errors));
        }

        var repository = inspection.Value!;
        if (!PathEquals(repository.ProjectRoot, workingDirectory))
        {
            return HookContext.Failed("Run this command from the Git repository root.");
        }

        if (!string.IsNullOrWhiteSpace(repository.CustomHooksPath))
        {
            return HookContext.Failed(
                $"Custom core.hooksPath '{repository.CustomHooksPath}' is not supported by Actio hooks.");
        }

        if (repository.IsLinkedWorktree)
        {
            return HookContext.Failed("Linked Git worktrees are not supported by Actio hooks yet.");
        }

        return HookContext.Resolved(Path.Combine(repository.GitDirectory, "hooks", "pre-push"));
    }

    private static GitHookState ReadState(string hookPath)
    {
        if (!File.Exists(hookPath))
        {
            return GitHookState.NotInstalled;
        }

        var actual = File.ReadAllText(hookPath)
            .ReplaceLineEndings("\n")
            .TrimEnd('\n');
        var expected = HookContent
            .ReplaceLineEndings("\n")
            .TrimEnd('\n');
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? GitHookState.Managed
            : GitHookState.Unmanaged;
    }

    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static GitHookResult Failed(string message, string? hookPath = null)
        => new(false, GitHookState.NotInstalled, message, hookPath);

    private sealed record HookContext(bool Success, string? HookPath, GitHookResult? Result)
    {
        public static HookContext Resolved(string hookPath) => new(true, hookPath, null);

        public static HookContext Failed(string message) => new(false, null, GitHookManager.Failed(message));
    }
}
