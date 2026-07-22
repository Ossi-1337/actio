namespace Actio.Engine.Execution;

public static class ContainerFilesystemPolicy
{
    public const string WorkspacePath = "/workspace";
    public const string ActionPath = "/actio/action";
    public const string EnvironmentPath = "/actio/env";

    private static readonly string[] ProtectedWorkspaceFiles =
    [
        ".actio/secrets.env",
        ".actio/vars.env"
    ];

    public static IReadOnlyList<string> ValidateMounts(
        string projectRoot,
        IEnumerable<StepExecutionMount> mounts)
    {
        var errors = new List<string>();
        if (projectRoot.Contains(',', StringComparison.Ordinal))
        {
            return [$"secure-baseline blocked project root '{projectRoot}' because comma-containing paths cannot be represented safely by Docker --mount."];
        }

        string canonicalProjectRoot;

        try
        {
            canonicalProjectRoot = FilesystemPathBoundary.ResolveExistingPath(projectRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [$"secure-baseline could not resolve project root: {ex.Message}"];
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            ValidateMount(canonicalProjectRoot, mount, targets, errors);
        }

        return errors;
    }

    public static string NormalizeContainerPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Container mount target '{path}' must be absolute.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException($"Container mount target '{path}' contains traversal segments.");
        }

        return "/" + string.Join('/', segments);
    }

    private static void ValidateMount(
        string canonicalProjectRoot,
        StepExecutionMount mount,
        HashSet<string> targets,
        List<string> errors)
    {
        string target;
        try
        {
            target = NormalizeContainerPath(mount.ContainerPath);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
            return;
        }

        if (mount.HostPath.Contains(',', StringComparison.Ordinal) || target.Contains(',', StringComparison.Ordinal))
        {
            errors.Add($"secure-baseline blocked mount '{mount.HostPath}' to '{target}' because comma-containing paths cannot be represented safely by Docker --mount.");
            return;
        }

        if (!targets.Add(target))
        {
            errors.Add($"secure-baseline blocked duplicate container mount target '{target}'.");
        }

        ValidateTarget(mount, target, errors);

        string source;
        try
        {
            source = FilesystemPathBoundary.ResolveExistingPath(mount.HostPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errors.Add($"secure-baseline blocked mount source '{mount.HostPath}': {ex.Message}");
            return;
        }

        if (IsContainerRuntimeSocket(source))
        {
            errors.Add($"secure-baseline blocked container runtime socket mount '{mount.HostPath}'.");
        }

        if (mount.Kind != StepExecutionMountKind.Workflow)
        {
            return;
        }

        if (!FilesystemPathBoundary.IsWithin(source, canonicalProjectRoot))
        {
            errors.Add($"secure-baseline blocked workflow mount source '{mount.HostPath}' because it resolves outside project root.");
            return;
        }

        var relative = Path.GetRelativePath(canonicalProjectRoot, source).Replace('\\', '/').Trim('/');
        if (relative.Length == 0 || relative == "." ||
            relative.Equals(".actio", StringComparison.OrdinalIgnoreCase) ||
            ProtectedWorkspaceFiles.Any(protectedPath =>
                relative.Equals(protectedPath, StringComparison.OrdinalIgnoreCase) ||
                protectedPath.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"secure-baseline blocked workflow mount source '{mount.HostPath}' because it exposes protected Actio value files.");
        }
    }

    private static void ValidateTarget(
        StepExecutionMount mount,
        string target,
        List<string> errors)
    {
        var valid = mount.Kind switch
        {
            StepExecutionMountKind.Workflow => !IsReservedTarget(target),
            StepExecutionMountKind.ActionSource => mount.ReadOnly && IsSameOrChild(target, ActionPath),
            StepExecutionMountKind.EnvironmentFiles => target == EnvironmentPath,
            StepExecutionMountKind.WorkspaceMask => mount.ReadOnly && ProtectedWorkspaceFiles.Any(path => target == $"/workspace/{path}"),
            _ => false
        };

        if (!valid)
        {
            errors.Add($"secure-baseline blocked mount target '{target}' for mount kind '{mount.Kind}'. Use a non-reserved container path.");
        }
    }

    private static bool IsReservedTarget(string target)
        => IsSameOrChild(target, WorkspacePath) || IsSameOrChild(target, "/actio");

    private static bool IsSameOrChild(string target, string root)
        => target == root || target.StartsWith(root + "/", StringComparison.Ordinal);

    private static bool IsContainerRuntimeSocket(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return fileName.Equals("docker.sock", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("podman.sock", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("containerd.sock", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("//./pipe/docker_engine", StringComparison.OrdinalIgnoreCase);
    }
}
