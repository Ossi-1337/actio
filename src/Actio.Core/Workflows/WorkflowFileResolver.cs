namespace Actio.Core.Workflows;

public sealed class WorkflowFileResolver
{
    public const string ActioWorkflowDirectoryName = ".workflows";
    public static readonly string GitHubWorkflowDirectoryName = Path.Combine(".github", "workflows");

    private static readonly char[] DirectorySeparators = ['/', '\\'];

    public WorkflowResolutionResult Resolve(string workflowName, string workingDirectory)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(workflowName))
        {
            errors.Add("Workflow name is required.");
            return WorkflowResolutionResult.Failed(errors);
        }

        if (Path.IsPathRooted(workflowName) || workflowName.IndexOfAny(DirectorySeparators) >= 0)
        {
            errors.Add("Milestone 02 supports bare workflow filenames only, for example 'ci.yml'.");
            return WorkflowResolutionResult.Failed(errors);
        }

        if (!workflowName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) &&
            !workflowName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Workflow filename must end with .yml or .yaml.");
            return WorkflowResolutionResult.Failed(errors);
        }

        var projectRoot = FindProjectRoot(workingDirectory);
        var actioWorkflowPath = Path.Combine(projectRoot, ActioWorkflowDirectoryName, workflowName);
        if (File.Exists(actioWorkflowPath))
        {
            return ResolveExistingWorkflow(projectRoot, actioWorkflowPath);
        }

        var gitHubWorkflowPath = Path.Combine(projectRoot, GitHubWorkflowDirectoryName, workflowName);
        if (File.Exists(gitHubWorkflowPath))
        {
            return ResolveExistingWorkflow(projectRoot, gitHubWorkflowPath);
        }

        errors.Add($"Workflow file was not found at '{actioWorkflowPath}' or '{gitHubWorkflowPath}'.");
        return WorkflowResolutionResult.Failed(errors);
    }

    public string FindProjectRoot(string workingDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ActioWorkflowDirectoryName)) ||
                Directory.Exists(Path.Combine(directory.FullName, GitHubWorkflowDirectoryName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(workingDirectory);
    }

    private static WorkflowResolutionResult ResolveExistingWorkflow(string projectRoot, string workflowPath)
    {
        try
        {
            var canonicalRoot = ResolveExistingPath(projectRoot);
            var canonicalWorkflow = ResolveExistingPath(workflowPath);
            return IsWithin(canonicalWorkflow, canonicalRoot)
                ? WorkflowResolutionResult.Resolved(projectRoot, canonicalWorkflow)
                : WorkflowResolutionResult.Failed(
                    [$"Workflow file '{workflowPath}' must resolve inside the project root."]);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            return WorkflowResolutionResult.Failed(
                [$"Workflow file '{workflowPath}' could not be resolved safely inside the project root."]);
        }
    }

    private static string ResolveExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path '{fullPath}' has no filesystem root.");
        var current = root;
        var remainder = fullPath[root.Length..];

        foreach (var segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = Directory.Exists(current)
                ? (FileSystemInfo)new DirectoryInfo(current)
                : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            current = Path.GetFullPath(
                info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new InvalidOperationException($"Reparse point '{current}' could not be resolved."));
        }

        return Path.GetFullPath(current);
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
