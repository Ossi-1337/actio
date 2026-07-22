using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal static class RunFilesystemIsolationPolicy
{
    internal static RunFilesystemIsolationResult Prepare(
        string projectRoot,
        RunStoragePaths storagePaths)
    {
        if (storagePaths.ActioHomePath is null || storagePaths.RunDirectory is null)
        {
            return RunFilesystemIsolationResult.Prepared(RunFilesystemIsolation.None);
        }

        try
        {
            var canonicalProjectRoot = FilesystemPathBoundary.ResolveExistingPath(projectRoot);
            var canonicalActioHome = FilesystemPathBoundary.ResolveExistingPath(storagePaths.ActioHomePath);
            if (FilesystemPathBoundary.IsWithin(canonicalActioHome, canonicalProjectRoot))
            {
                return RunFilesystemIsolationResult.Failed(
                    $"secure-baseline blocked execution because ACTIO_HOME '{canonicalActioHome}' is inside project root. Move ACTIO_HOME to user-local storage.");
            }

            var projectActioDirectory = Path.Combine(projectRoot, ".actio");
            if (Directory.Exists(projectActioDirectory))
            {
                var canonicalProjectActioDirectory = FilesystemPathBoundary.ResolveExistingPath(projectActioDirectory);
                if (!FilesystemPathBoundary.IsWithin(canonicalProjectActioDirectory, canonicalProjectRoot))
                {
                    return RunFilesystemIsolationResult.Failed(
                        $"secure-baseline blocked project value directory '{projectActioDirectory}' because it resolves outside project root.");
                }
            }

            var mounts = new List<StepExecutionMount>();
            foreach (var (relativePath, maskPath) in storagePaths.WorkspaceMaskFiles)
            {
                var protectedPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(protectedPath))
                {
                    continue;
                }

                var canonicalProtectedPath = FilesystemPathBoundary.ResolveExistingPath(protectedPath);
                if (!FilesystemPathBoundary.IsWithin(canonicalProtectedPath, canonicalProjectRoot))
                {
                    return RunFilesystemIsolationResult.Failed(
                        $"secure-baseline blocked protected workspace file '{protectedPath}' because it resolves outside project root.");
                }

                mounts.Add(new StepExecutionMount(
                    maskPath,
                    $"/workspace/{relativePath}",
                    ReadOnly: true,
                    StepExecutionMountKind.WorkspaceMask));
            }

            var stagingRoot = Path.Combine(storagePaths.RunDirectory, "build-contexts");
            Directory.CreateDirectory(stagingRoot);
            return RunFilesystemIsolationResult.Prepared(new RunFilesystemIsolation(mounts, stagingRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return RunFilesystemIsolationResult.Failed(
                $"secure-baseline could not prepare filesystem isolation: {ex.Message}");
        }
    }
}

internal sealed record RunFilesystemIsolationResult(
    bool Success,
    RunFilesystemIsolation Isolation,
    IReadOnlyList<string> Errors)
{
    internal static RunFilesystemIsolationResult Prepared(RunFilesystemIsolation isolation)
        => new(true, isolation, []);

    internal static RunFilesystemIsolationResult Failed(string error)
        => new(false, RunFilesystemIsolation.None, [error]);
}
