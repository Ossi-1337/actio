using Actio.Engine.Execution;

namespace Actio.Runner.Docker;

internal static class DockerfileBuildContextPreparer
{
    private static readonly string[] ExcludedDirectoryNames = [".git", ".hg", ".svn"];
    private static readonly string[] ExcludedFiles = [".actio/secrets.env", ".actio/vars.env"];

    internal static DockerfileBuildContextResult Prepare(DockerfileActionExecutionRequest request)
    {
        try
        {
            var sourceRoot = FilesystemPathBoundary.ResolveExistingPath(request.BuildContext);
            var dockerfile = FilesystemPathBoundary.ResolveExistingPath(request.DockerfilePath);
            if (!FilesystemPathBoundary.IsWithin(dockerfile, sourceRoot))
            {
                return DockerfileBuildContextResult.Failed(
                    $"secure-baseline blocked Dockerfile '{request.DockerfilePath}' because it resolves outside the action build context.");
            }

            var stagingRoot = request.BuildContextStagingRoot ?? Path.Combine(
                Path.GetTempPath(),
                "actio-build-contexts");
            Directory.CreateDirectory(stagingRoot);
            var destinationRoot = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destinationRoot);

            CopyDirectory(sourceRoot, destinationRoot);

            var dockerfileRelativePath = Path.GetRelativePath(sourceRoot, dockerfile);
            var stagedDockerfile = Path.Combine(destinationRoot, dockerfileRelativePath);
            if (!File.Exists(stagedDockerfile))
            {
                Directory.Delete(destinationRoot, recursive: true);
                return DockerfileBuildContextResult.Failed(
                    "secure-baseline excluded the configured Dockerfile from the staged action build context.");
            }

            return DockerfileBuildContextResult.Prepared(
                request with
                {
                    BuildContext = destinationRoot,
                    DockerfilePath = stagedDockerfile
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return DockerfileBuildContextResult.Failed(
                $"secure-baseline could not stage Dockerfile action build context: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        var pending = new Queue<(string Source, string Destination)>();
        pending.Enqueue((sourceRoot, destinationRoot));

        while (pending.Count > 0)
        {
            var (source, destination) = pending.Dequeue();
            foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            {
                var relativePath = Path.GetRelativePath(sourceRoot, entry.FullName).Replace('\\', '/');
                if (ShouldExclude(entry, relativePath))
                {
                    continue;
                }

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Reparse point '{entry.FullName}' is not allowed in Dockerfile action build contexts.");
                }

                var destinationPath = Path.Combine(destination, entry.Name);
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(destinationPath);
                    pending.Enqueue((entry.FullName, destinationPath));
                }
                else
                {
                    File.Copy(entry.FullName, destinationPath, overwrite: false);
                }
            }
        }
    }

    private static bool ShouldExclude(FileSystemInfo entry, string relativePath)
    {
        if (entry is DirectoryInfo && ExcludedDirectoryNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExcludedFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record DockerfileBuildContextResult(
    bool Success,
    DockerfileActionExecutionRequest? Request,
    string? Error)
{
    internal static DockerfileBuildContextResult Prepared(DockerfileActionExecutionRequest request)
        => new(true, request, null);

    internal static DockerfileBuildContextResult Failed(string error)
        => new(false, null, error);
}
