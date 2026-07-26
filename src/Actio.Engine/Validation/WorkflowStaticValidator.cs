using Actio.Core.Actions;
using Actio.Core.Workflows;
using Actio.Engine.Execution;
using Actio.Engine.Setup;
using System.Text.RegularExpressions;

namespace Actio.Engine.Validation;

public sealed record WorkflowValidationDiagnostic(string SourcePath, string Message);

public sealed record WorkflowStaticValidationResult(
    bool Success,
    WorkflowDocument? Workflow,
    IReadOnlyList<WorkflowValidationDiagnostic> Errors,
    IReadOnlyList<WorkflowValidationDiagnostic> Warnings);

public sealed partial class WorkflowStaticValidator
{
    private const int MaxReferenceDepth = 10;
    private readonly WorkflowParser _workflowParser = new();
    private readonly ActionParser _actionParser = new();

    public WorkflowStaticValidationResult Validate(
        string workflowPath,
        string projectRoot,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> secrets)
    {
        var errors = new List<WorkflowValidationDiagnostic>();
        var warnings = new List<WorkflowValidationDiagnostic>();
        var canonicalRoot = FilesystemPathBoundary.ResolveExistingPath(projectRoot);
        var canonicalWorkflow = FilesystemPathBoundary.ResolveExistingPath(workflowPath);
        var source = FormatSource(canonicalWorkflow, canonicalRoot);
        var parseResult = _workflowParser.ParseFile(canonicalWorkflow);
        Add(errors, source, parseResult.Errors);
        Add(warnings, source, parseResult.Warnings);
        if (!parseResult.Success)
        {
            return Result(null, errors, warnings);
        }

        var workflow = parseResult.Workflow!;
        Add(errors, source, WorkflowDispatchInputResolver.ValidateProvided(workflow, inputs).Errors);
        ValidateExplicitSecretReferences(workflow, secrets, source, errors);

        var environment = WorkflowEnvironmentResolver.Resolve(workflow, secrets);
        Add(errors, source, environment.Errors);
        var expanded = MatrixJobExpander.Expand(workflow.Jobs);
        Add(errors, source, expanded.Errors);
        if (expanded.Errors.Count == 0)
        {
            Add(errors, source, JobGraphPlanner.Plan(expanded.Jobs).Errors);
        }

        var workflowStack = new HashSet<string>(PathComparer) { canonicalWorkflow };
        var actionStack = new HashSet<string>(PathComparer);
        ValidateWorkflowReferences(
            workflow,
            canonicalWorkflow,
            canonicalRoot,
            0,
            workflowStack,
            actionStack,
            errors,
            warnings);

        return Result(workflow, errors, warnings);
    }

    private void ValidateWorkflowReferences(
        WorkflowDocument workflow,
        string workflowPath,
        string projectRoot,
        int depth,
        HashSet<string> workflowStack,
        HashSet<string> actionStack,
        List<WorkflowValidationDiagnostic> errors,
        List<WorkflowValidationDiagnostic> warnings)
    {
        var source = FormatSource(workflowPath, projectRoot);
        foreach (var job in workflow.Jobs.Values)
        {
            if (job.Call is not null)
            {
                ValidateReusableWorkflow(
                    job,
                    workflowPath,
                    projectRoot,
                    depth,
                    workflowStack,
                    actionStack,
                    errors,
                    warnings);
                continue;
            }

            foreach (var step in job.Steps.Where(step => step.Uses is not null))
            {
                ValidateActionReference(
                    step.Uses!,
                    step.With,
                    projectRoot,
                    projectRoot,
                    depth,
                    actionStack,
                    errors,
                    warnings,
                    source);
            }
        }
    }

    private void ValidateReusableWorkflow(
        WorkflowJob caller,
        string callerPath,
        string projectRoot,
        int depth,
        HashSet<string> workflowStack,
        HashSet<string> actionStack,
        List<WorkflowValidationDiagnostic> errors,
        List<WorkflowValidationDiagnostic> warnings)
    {
        var source = FormatSource(callerPath, projectRoot);
        if (depth >= MaxReferenceDepth)
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name}.uses exceeds the reusable workflow depth limit of {MaxReferenceDepth}."));
            return;
        }

        var path = ResolveLocalPath(projectRoot, caller.Call!.Uses, projectRoot);
        if (path is null || !IsReusableWorkflowPath(path, projectRoot))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name}.uses must reference a workflow under .workflows/ or .github/workflows/."));
            return;
        }

        if (!File.Exists(path))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name}.uses references missing workflow '{caller.Call.Uses}'."));
            return;
        }

        if (!workflowStack.Add(path))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name}.uses creates a reusable workflow cycle."));
            return;
        }

        try
        {
            var parsed = _workflowParser.ParseFile(path);
            var calleeSource = FormatSource(path, projectRoot);
            Add(errors, calleeSource, parsed.Errors);
            Add(warnings, calleeSource, parsed.Warnings);
            if (!parsed.Success)
            {
                return;
            }

            var callee = parsed.Workflow!;
            var call = callee.Triggers
                .FirstOrDefault(trigger => string.Equals(trigger.EventName, "workflow_call", StringComparison.Ordinal))?
                .Call;
            if (call is null)
            {
                errors.Add(new(calleeSource, "Reusable workflow does not declare on.workflow_call."));
                return;
            }

            ValidateWorkflowCallBindings(caller, call, source, errors);
            var expanded = MatrixJobExpander.Expand(callee.Jobs);
            Add(errors, calleeSource, expanded.Errors);
            if (expanded.Errors.Count == 0)
            {
                Add(errors, calleeSource, JobGraphPlanner.Plan(expanded.Jobs).Errors);
            }

            ValidateWorkflowReferences(
                callee,
                path,
                projectRoot,
                depth + 1,
                workflowStack,
                actionStack,
                errors,
                warnings);
        }
        finally
        {
            workflowStack.Remove(path);
        }
    }

    private void ValidateActionReference(
        string uses,
        IReadOnlyDictionary<string, string> with,
        string referenceRoot,
        string projectRoot,
        int depth,
        HashSet<string> actionStack,
        List<WorkflowValidationDiagnostic> errors,
        List<WorkflowValidationDiagnostic> warnings,
        string source)
    {
        if (!ActionReference.TryParse(uses, out var reference))
        {
            errors.Add(new(source, $"Action reference '{uses}' is invalid."));
            return;
        }

        if (reference!.Kind == ActionReferenceKind.Local)
        {
            ValidateLocalAction(uses, with, referenceRoot, projectRoot, depth, actionStack, errors, warnings, source);
            return;
        }

        if (reference.Kind == ActionReferenceKind.GitHubRepository)
        {
            var compatibility = KnownActionCompatibilityCatalog.Find(uses);
            if (compatibility?.Status == ActionCompatibilityStatus.Unsupported)
            {
                errors.Add(new(source, compatibility.FormatUnsupportedMessage(uses)));
            }
            else if (compatibility is null)
            {
                warnings.Add(new(source, $"Action '{uses}' has valid external syntax, but its metadata was not inspected during static validation."));
            }
            else
            {
                ValidateKnownActionInputs(uses, with, source, errors);
            }
        }
        else if (reference.Kind == ActionReferenceKind.DockerImage)
        {
            warnings.Add(new(source, $"Docker action '{uses}' has valid external syntax, but its image metadata was not inspected during static validation."));
        }
    }

    private static void ValidateExplicitSecretReferences(
        WorkflowDocument workflow,
        IReadOnlyDictionary<string, string> secrets,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        foreach (var job in workflow.Jobs.Values)
        {
            foreach (var step in job.Steps)
            {
                ValidateSecretMap(step.With, $"workflow.jobs.{job.Name}.steps.{step.Name}.with", secrets, source, errors);
            }

            if (job.Call is not null)
            {
                ValidateSecretMap(job.Call.With, $"workflow.jobs.{job.Name}.with", secrets, source, errors);
                ValidateSecretMap(job.Call.Secrets, $"workflow.jobs.{job.Name}.secrets", secrets, source, errors);
            }
        }
    }

    private static void ValidateSecretMap(
        IReadOnlyDictionary<string, string> values,
        string path,
        IReadOnlyDictionary<string, string> secrets,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        foreach (var value in values)
        {
            foreach (Match match in SecretReferencePattern().Matches(value.Value))
            {
                var name = match.Groups[1].Value;
                if (!secrets.ContainsKey(name))
                {
                    errors.Add(new(source, $"{path}.{value.Key} references missing secret '{name}'."));
                }
            }
        }
    }

    private static void ValidateKnownActionInputs(
        string uses,
        IReadOnlyDictionary<string, string> with,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        var setup = SetupActionResolver.Resolve(uses, with);
        if (setup.Action is not null || !setup.Success)
        {
            Add(errors, source, setup.Errors);
            return;
        }

        if (!ActionReference.TryParse(uses, out var reference) ||
            !reference!.TryGetGitHubAction(out var action))
        {
            return;
        }

        var name = $"{action!.Owner}/{action.Repository}";
        IReadOnlySet<string>? allowed = name.ToLowerInvariant() switch
        {
            "actions/checkout" => new HashSet<string>(StringComparer.Ordinal),
            "actions/cache" => new HashSet<string>(["path", "key", "restore-keys"], StringComparer.Ordinal),
            "actions/upload-artifact" => new HashSet<string>(["name", "path", "retention-days"], StringComparer.Ordinal),
            "actions/download-artifact" => new HashSet<string>(["name", "path"], StringComparer.Ordinal),
            _ => null
        };
        if (allowed is null)
        {
            return;
        }

        foreach (var input in with.Keys.Where(input => !allowed.Contains(input)))
        {
            errors.Add(new(source, $"{name} with.{input} is not supported by Actio."));
        }

        if (name.Equals("actions/cache", StringComparison.OrdinalIgnoreCase))
        {
            AddRequiredInputError(with, "path", name, source, errors);
            AddRequiredInputError(with, "key", name, source, errors);
        }
        else if (name.Equals("actions/upload-artifact", StringComparison.OrdinalIgnoreCase))
        {
            AddRequiredInputError(with, "path", name, source, errors);
        }
    }

    private static void AddRequiredInputError(
        IReadOnlyDictionary<string, string> with,
        string input,
        string action,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        if (!with.TryGetValue(input, out var value) || string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(source, $"{action} requires with.{input}."));
        }
    }

    private void ValidateLocalAction(
        string uses,
        IReadOnlyDictionary<string, string> with,
        string referenceRoot,
        string projectRoot,
        int depth,
        HashSet<string> actionStack,
        List<WorkflowValidationDiagnostic> errors,
        List<WorkflowValidationDiagnostic> warnings,
        string source)
    {
        if (depth >= MaxReferenceDepth)
        {
            errors.Add(new(source, $"Action '{uses}' exceeds the local action depth limit of {MaxReferenceDepth}."));
            return;
        }

        var path = ResolveLocalActionPath(referenceRoot, uses, projectRoot);
        if (path is null)
        {
            errors.Add(new(source, $"Local action '{uses}' is missing or escapes the project root."));
            return;
        }

        if (!actionStack.Add(path))
        {
            errors.Add(new(source, $"Local action '{uses}' creates an action reference cycle."));
            return;
        }

        try
        {
            var parsed = _actionParser.ParseFile(path);
            var actionSource = FormatSource(path, projectRoot);
            Add(errors, actionSource, parsed.Errors);
            if (!parsed.Success)
            {
                return;
            }

            var action = parsed.Action!;
            ValidateActionInputs(action, with, actionSource, errors);
            ValidateActionEntrypoints(action, Path.GetDirectoryName(path)!, actionSource, errors);
            foreach (var step in action.Steps.Where(step => step.Uses is not null))
            {
                ValidateActionReference(
                    step.Uses!,
                    step.With,
                    Path.GetDirectoryName(path)!,
                    projectRoot,
                    depth + 1,
                    actionStack,
                    errors,
                    warnings,
                    actionSource);
            }
        }
        finally
        {
            actionStack.Remove(path);
        }
    }

    private static void ValidateActionInputs(
        ActionDocument action,
        IReadOnlyDictionary<string, string> values,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        foreach (var name in values.Keys.Where(name => !action.Inputs.ContainsKey(name)))
        {
            errors.Add(new(source, $"Action input '{name}' is not declared."));
        }

        foreach (var input in action.Inputs.Values.Where(input =>
                     input.Required && input.Default is null && !values.ContainsKey(input.Name)))
        {
            errors.Add(new(source, $"Required action input '{input.Name}' is missing."));
        }
    }

    private static void ValidateActionEntrypoints(
        ActionDocument action,
        string actionRoot,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        if (action.Runtime is ActionRuntime.Node20 or ActionRuntime.Node24)
        {
            foreach (var entrypoint in new[] { action.Main, action.Pre, action.Post }.Where(value => value is not null))
            {
                ValidateEntrypoint(entrypoint!, actionRoot, source, errors);
            }
        }
        else if (action.Runtime == ActionRuntime.Docker &&
                 action.Image is not null &&
                 !action.Image.StartsWith("docker://", StringComparison.Ordinal))
        {
            ValidateEntrypoint(action.Image, actionRoot, source, errors);
        }
    }

    private static void ValidateEntrypoint(
        string relativePath,
        string actionRoot,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        var path = ResolveLocalPath(actionRoot, relativePath, actionRoot);
        if (path is null || !File.Exists(path))
        {
            errors.Add(new(source, $"Action entrypoint '{relativePath}' is missing or escapes the action root."));
        }
    }

    private static void ValidateWorkflowCallBindings(
        WorkflowJob caller,
        WorkflowCall call,
        string source,
        List<WorkflowValidationDiagnostic> errors)
    {
        foreach (var input in caller.Call!.With.Keys.Where(name => !call.Inputs.ContainsKey(name)))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name}.with.{input} is not declared by the reusable workflow."));
        }

        foreach (var input in call.Inputs.Values.Where(input =>
                     input.Required && input.Default is null && !caller.Call.With.ContainsKey(input.Name)))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name} is missing required reusable workflow input '{input.Name}'."));
        }

        foreach (var secret in caller.Call.Secrets.Keys.Where(name => !call.Secrets.ContainsKey(name)))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name}.secrets.{secret} is not declared by the reusable workflow."));
        }

        foreach (var secret in call.Secrets.Values.Where(secret =>
                     secret.Required && !caller.Call.Secrets.ContainsKey(secret.Name)))
        {
            errors.Add(new(source, $"workflow.jobs.{caller.Name} is missing required reusable workflow secret '{secret.Name}'."));
        }
    }

    private static string? ResolveLocalActionPath(string root, string uses, string projectRoot)
    {
        var path = ResolveLocalPath(root, uses[2..], projectRoot);
        if (path is null)
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        foreach (var name in new[] { "action.yml", "action.yaml" })
        {
            var candidate = Path.Combine(path, name);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? ResolveLocalPath(string root, string relativePath, string boundary)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var canonicalBoundary = FilesystemPathBoundary.ResolveExistingPath(boundary);
            if (!FilesystemPathBoundary.IsWithin(fullPath, boundary))
            {
                return null;
            }

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                var canonicalPath = FilesystemPathBoundary.ResolveExistingPath(fullPath);
                return FilesystemPathBoundary.IsWithin(canonicalPath, canonicalBoundary)
                    ? canonicalPath
                    : null;
            }

            var ancestor = Path.GetDirectoryName(fullPath);
            while (ancestor is not null && !Directory.Exists(ancestor))
            {
                ancestor = Path.GetDirectoryName(ancestor);
            }

            return ancestor is not null &&
                   FilesystemPathBoundary.IsWithin(
                       FilesystemPathBoundary.ResolveExistingPath(ancestor),
                       canonicalBoundary)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsReusableWorkflowPath(string path, string projectRoot)
    {
        var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
        return relative.StartsWith(".workflows/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSource(string path, string projectRoot)
    {
        var relative = Path.GetRelativePath(projectRoot, path);
        return relative.Replace('\\', '/');
    }

    private static void Add(
        List<WorkflowValidationDiagnostic> target,
        string source,
        IEnumerable<string> messages)
    {
        target.AddRange(messages.Select(message => new WorkflowValidationDiagnostic(source, message)));
    }

    private static WorkflowStaticValidationResult Result(
        WorkflowDocument? workflow,
        List<WorkflowValidationDiagnostic> errors,
        List<WorkflowValidationDiagnostic> warnings)
    {
        var distinctErrors = errors.Distinct().ToArray();
        var distinctWarnings = warnings.Distinct().ToArray();
        return new WorkflowStaticValidationResult(
            distinctErrors.Length == 0,
            workflow,
            distinctErrors,
            distinctWarnings);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"\$\{\{\s*secrets\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();
}
