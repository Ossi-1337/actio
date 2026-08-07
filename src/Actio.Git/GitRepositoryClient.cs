namespace Actio.Git;

public sealed record GitRepositoryInfo(
    string ProjectRoot,
    string GitDirectory,
    string CommonGitDirectory,
    string? CustomHooksPath)
{
    public bool IsLinkedWorktree => !PathEquals(GitDirectory, CommonGitDirectory);

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record GitOperationResult<T>(bool Success, T? Value, IReadOnlyList<string> Errors)
{
    public static GitOperationResult<T> Succeeded(T value) => new(true, value, []);

    public static GitOperationResult<T> Failed(params string[] errors) => new(false, default, errors);
}

public interface IGitRepositoryClient
{
    Task<GitOperationResult<GitRepositoryInfo>> InspectAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<GitOperationResult<bool>> IsCleanAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task<GitOperationResult<string>> GetHeadAsync(
        string projectRoot,
        CancellationToken cancellationToken = default);

    Task<GitOperationResult<IReadOnlyList<string>>> GetChangedPathsAsync(
        string projectRoot,
        GitPushRefUpdate update,
        CancellationToken cancellationToken = default);
}

public sealed class GitRepositoryClient : IGitRepositoryClient
{
    private readonly IGitCommandRunner _runner;

    public GitRepositoryClient(IGitCommandRunner? runner = null)
    {
        _runner = runner ?? new GitCommandRunner();
    }

    public async Task<GitOperationResult<GitRepositoryInfo>> InspectAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var rootResult = await RunRequiredAsync(
            workingDirectory,
            ["rev-parse", "--show-toplevel"],
            "Current directory is not inside a Git repository.",
            cancellationToken);
        if (!rootResult.Success)
        {
            return GitOperationResult<GitRepositoryInfo>.Failed(rootResult.Errors.ToArray());
        }

        var projectRoot = Path.GetFullPath(rootResult.Value!);
        var gitDirectoryResult = await RunRequiredAsync(
            projectRoot,
            ["rev-parse", "--git-dir"],
            "Git directory could not be resolved.",
            cancellationToken);
        var commonDirectoryResult = await RunRequiredAsync(
            projectRoot,
            ["rev-parse", "--git-common-dir"],
            "Common Git directory could not be resolved.",
            cancellationToken);
        if (!gitDirectoryResult.Success || !commonDirectoryResult.Success)
        {
            return GitOperationResult<GitRepositoryInfo>.Failed(
                gitDirectoryResult.Errors.Concat(commonDirectoryResult.Errors).ToArray());
        }

        var hooksPathResult = await _runner.RunAsync(
            projectRoot,
            ["config", "--get", "core.hooksPath"],
            cancellationToken);
        if (!hooksPathResult.Success && hooksPathResult.ExitCode is not 1)
        {
            return GitOperationResult<GitRepositoryInfo>.Failed(
                CreateGitError("Git core.hooksPath configuration could not be read.", hooksPathResult));
        }

        var hooksPath = hooksPathResult.Success
            ? hooksPathResult.StandardOutput.Trim()
            : null;

        return GitOperationResult<GitRepositoryInfo>.Succeeded(new GitRepositoryInfo(
            projectRoot,
            ResolveGitPath(projectRoot, gitDirectoryResult.Value!),
            ResolveGitPath(projectRoot, commonDirectoryResult.Value!),
            string.IsNullOrWhiteSpace(hooksPath) ? null : hooksPath));
    }

    public async Task<GitOperationResult<bool>> IsCleanAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            projectRoot,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken);
        return result.Success
            ? GitOperationResult<bool>.Succeeded(string.IsNullOrWhiteSpace(result.StandardOutput))
            : GitOperationResult<bool>.Failed(CreateGitError("Git worktree status could not be read.", result));
    }

    public async Task<GitOperationResult<string>> GetHeadAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        return await RunRequiredAsync(
            projectRoot,
            ["rev-parse", "HEAD"],
            "Current Git HEAD could not be resolved.",
            cancellationToken);
    }

    public async Task<GitOperationResult<IReadOnlyList<string>>> GetChangedPathsAsync(
        string projectRoot,
        GitPushRefUpdate update,
        CancellationToken cancellationToken = default)
    {
        var arguments = update.IsNewRef
            ? new[] { "ls-tree", "-r", "--name-only", "-z", update.LocalObjectId }
            : new[] { "diff", "--name-only", "--diff-filter=ACDMRTUXB", "-z", update.RemoteObjectId, update.LocalObjectId };
        var result = await _runner.RunAsync(projectRoot, arguments, cancellationToken);
        if (!result.Success)
        {
            return GitOperationResult<IReadOnlyList<string>>.Failed(
                CreateGitError($"Changed paths for '{update.RemoteRef}' could not be resolved.", result));
        }

        var paths = result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return GitOperationResult<IReadOnlyList<string>>.Succeeded(paths);
    }

    private async Task<GitOperationResult<string>> RunRequiredAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(workingDirectory, arguments, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return GitOperationResult<string>.Failed(CreateGitError(failureMessage, result));
        }

        return GitOperationResult<string>.Succeeded(result.StandardOutput.Trim());
    }

    private static string ResolveGitPath(string projectRoot, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));

    private static string CreateGitError(string message, GitCommandResult result)
    {
        var detail = result.StandardError.Trim();
        return detail.Length == 0 ? message : $"{message} {detail}";
    }
}
