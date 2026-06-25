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
            return WorkflowResolutionResult.Resolved(projectRoot, actioWorkflowPath);
        }

        var gitHubWorkflowPath = Path.Combine(projectRoot, GitHubWorkflowDirectoryName, workflowName);
        if (File.Exists(gitHubWorkflowPath))
        {
            return WorkflowResolutionResult.Resolved(projectRoot, gitHubWorkflowPath);
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
}
