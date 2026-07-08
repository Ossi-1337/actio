using System.Security.Cryptography;
using System.Text;
using Actio.Core.Actions;
using Actio.Core.Workflows;
using Actio.Engine.Actions;

namespace Actio.Engine.Execution;

internal sealed class ActionResolver
{
    private const string ActionContainerPath = "/actio/action";
    private const string DockerfileImageRepository = "actio/action";
    private const string CheckoutShimCommand = "printf '%s\\n' 'Actio checkout shim: workspace is already available at /workspace.'";
    private const int MaxNestedActionDepth = 10;

    private readonly ActionParser _parser;
    private readonly IActionCache _cache;
    private readonly IGitHubActionSourceProvider _githubActionSourceProvider;

    public ActionResolver(
        ActionParser parser,
        IActionCache cache,
        IGitHubActionSourceProvider githubActionSourceProvider)
    {
        _parser = parser;
        _cache = cache;
        _githubActionSourceProvider = githubActionSourceProvider;
    }

    public async Task<ActionResolutionResult> ResolveAsync(
        WorkflowStep step,
        string projectRoot,
        CancellationToken cancellationToken)
        => await ResolveAsync(
            step.Uses,
            step.With,
            projectRoot,
            projectRoot,
            [],
            cancellationToken);

    private async Task<ActionResolutionResult> ResolveAsync(
        string? uses,
        IReadOnlyDictionary<string, string> with,
        string projectRoot,
        string localReferenceRoot,
        IReadOnlyList<ActionResolutionFrame> stack,
        CancellationToken cancellationToken)
    {
        if (uses is null)
        {
            return ActionResolutionResult.Failed(["Step does not define uses."]);
        }

        if (!ActionReference.TryParse(uses, out var reference))
        {
            return ActionResolutionResult.Failed([$"uses '{uses}' is not supported. Supported formats are './...', 'docker://...', and 'owner/repo[/path]@ref'."]);
        }

        var warnings = reference!.IsMutable
            ? new[] { FormatMutableReferenceWarning(reference) }
            : [];

        return reference!.Kind switch
        {
            ActionReferenceKind.Local => AddWarnings(
                await ResolveLocalActionAsync(uses, with, projectRoot, localReferenceRoot, stack, cancellationToken),
                warnings),
            ActionReferenceKind.DockerImage => AddWarnings(
                await ResolveDockerImageActionAsync(uses, with, reference, cancellationToken),
                warnings),
            ActionReferenceKind.GitHubRepository => AddWarnings(
                await ResolveGitHubActionAsync(uses, with, reference, projectRoot, stack, cancellationToken),
                warnings),
            _ => ActionResolutionResult.Failed([$"uses '{uses}' is not supported."])
        };
    }

    private async Task<ActionResolutionResult> ResolveLocalActionAsync(
        string uses,
        IReadOnlyDictionary<string, string> with,
        string projectRoot,
        string localReferenceRoot,
        IReadOnlyList<ActionResolutionFrame> stack,
        CancellationToken cancellationToken)
    {
        var actionPathResult = ResolveLocalActionPath(uses, localReferenceRoot);
        if (!actionPathResult.Success)
        {
            return ActionResolutionResult.Failed(actionPathResult.Errors);
        }

        var cycleCheck = CheckCycleAndDepth(actionPathResult.ActionPath!, stack);
        if (!cycleCheck.Success)
        {
            return ActionResolutionResult.Failed(cycleCheck.Errors);
        }

        var parseResult = _parser.ParseFile(actionPathResult.ActionPath!);
        if (!parseResult.Success)
        {
            return ActionResolutionResult.Failed(parseResult.Errors);
        }

        var inputBinding = ActionInputBinder.Bind(parseResult.Action!, with);
        if (!inputBinding.Success)
        {
            return ActionResolutionResult.Failed(inputBinding.Errors);
        }

        var actionDirectory = Path.GetDirectoryName(actionPathResult.ActionPath!)!;
        var actionStack = AddFrame(stack, actionPathResult.ActionPath!, uses);
        CompositeActionPlanResult? compositePlan = null;
        if (string.Equals(parseResult.Action!.Runtime, ActionRuntime.Composite, StringComparison.Ordinal))
        {
            compositePlan = await BuildCompositeStepsAsync(
                parseResult.Action,
                inputBinding.Inputs,
                projectRoot,
                actionDirectory,
                actionStack,
                cancellationToken);
            if (!compositePlan.Success)
            {
                return ActionResolutionResult.Failed(compositePlan.Errors);
            }
        }

        if (string.Equals(parseResult.Action!.Runtime, ActionRuntime.Docker, StringComparison.Ordinal))
        {
            return await ResolveDockerfileActionAsync(
                uses,
                parseResult.Action,
                inputBinding,
                actionDirectory,
                projectRoot,
                null,
                null,
                cancellationToken);
        }

        try
        {
            var contentHash = await ComputeContentHashAsync(actionPathResult.ActionPath!, cancellationToken);
            var cacheEntry = await _cache.GetOrAddLocalActionAsync(
                new LocalActionCacheRequest(uses, actionPathResult.ActionPath!, contentHash),
                cancellationToken);

            if (compositePlan is not null)
            {
                return CreateResolvedCompositeAction(
                    parseResult.Action!,
                    cacheEntry,
                    inputBinding,
                    actionDirectory,
                    compositePlan,
                    []);
            }

            return await ResolveParsedActionAsync(
                parseResult.Action!,
                cacheEntry,
                inputBinding,
                actionDirectory,
                projectRoot,
                actionStack,
                [],
                cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching action '{uses}'", ex)]);
        }
    }

    private async Task<ActionResolutionResult> ResolveDockerImageActionAsync(
        string uses,
        IReadOnlyDictionary<string, string> with,
        ActionReference reference,
        CancellationToken cancellationToken)
    {
        var dockerOptions = DockerImageActionOptions.FromInputs(with);
        if (!dockerOptions.Success)
        {
            return ActionResolutionResult.Failed(dockerOptions.Errors);
        }

        try
        {
            var cacheEntry = await _cache.GetOrAddDockerImageActionAsync(
                new DockerImageActionCacheRequest(uses, reference.Target, reference.IsPinned, reference.MutablePart),
                cancellationToken);

            return ActionResolutionResult.ResolvedDockerImage(
                uses,
                reference.Target,
                cacheEntry,
                ActionInputBinder.CreateEnvironment(with),
                dockerOptions.EntryPoint,
                dockerOptions.Arguments);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching action '{uses}'", ex)]);
        }
    }

    private async Task<ActionResolutionResult> ResolveGitHubActionAsync(
        string uses,
        IReadOnlyDictionary<string, string> with,
        ActionReference reference,
        string projectRoot,
        IReadOnlyList<ActionResolutionFrame> stack,
        CancellationToken cancellationToken)
    {
        if (!reference.TryGetGitHubAction(out var githubAction))
        {
            return ActionResolutionResult.Failed([$"uses '{uses}' is not a valid GitHub action reference."]);
        }

        var compatibility = KnownActionCompatibilityCatalog.Find(githubAction!);
        if (compatibility?.Status == ActionCompatibilityStatus.Unsupported)
        {
            return ActionResolutionResult.Failed([compatibility.FormatUnsupportedMessage(uses)]);
        }

        if (IsCheckoutAction(githubAction!) && !IsCheckoutShim(githubAction!))
        {
            return ActionResolutionResult.Failed(["actions/checkout is supported only as the Actio actions/checkout@v4 local checkout shim. Use actions/checkout@v4 without with: inputs. See the compatibility matrix for current limitations."]);
        }

        if (IsCheckoutShim(githubAction!))
        {
            if (with.Count > 0)
            {
                return ActionResolutionResult.Failed(["actions/checkout@v4 with inputs is not supported by the Actio checkout shim yet."]);
            }

            return ActionResolutionResult.ResolvedBuiltInAction(CheckoutShimCommand);
        }

        var sourceResult = await _githubActionSourceProvider.GetGitHubActionSourceAsync(
            new GitHubActionSourceRequest(
                uses,
                githubAction!.Owner,
                githubAction.Repository,
                githubAction.ActionPath,
                githubAction.Ref,
                reference.IsPinned,
                reference.MutablePart),
            cancellationToken);

        if (!sourceResult.Success)
        {
            return ActionResolutionResult.Failed(sourceResult.Errors);
        }

        var cycleCheck = CheckCycleAndDepth(sourceResult.ActionFilePath!, stack);
        if (!cycleCheck.Success)
        {
            return ActionResolutionResult.Failed(cycleCheck.Errors);
        }

        var parseResult = _parser.ParseFile(sourceResult.ActionFilePath!);
        if (!parseResult.Success)
        {
            return ActionResolutionResult.Failed(parseResult.Errors);
        }

        var inputBinding = ActionInputBinder.Bind(parseResult.Action!, with);
        if (!inputBinding.Success)
        {
            return ActionResolutionResult.Failed(inputBinding.Errors);
        }

        if (string.Equals(parseResult.Action!.Runtime, ActionRuntime.Docker, StringComparison.Ordinal))
        {
            return await ResolveDockerfileActionAsync(
                uses,
                parseResult.Action,
                inputBinding,
                sourceResult.ActionDirectory!,
                projectRoot,
                sourceResult.CacheEntry!.PinnedIdentity,
                sourceResult.CacheEntry.MutablePart,
                cancellationToken);
        }

        var actionStack = AddFrame(stack, sourceResult.ActionFilePath!, uses);
        return await ResolveParsedActionAsync(
            parseResult.Action!,
            sourceResult.CacheEntry!,
            inputBinding,
            sourceResult.ActionDirectory!,
            projectRoot,
            actionStack,
            [],
            cancellationToken);
    }

    private static bool IsCheckoutShim(GitHubActionReference action)
    {
        return IsCheckoutAction(action) &&
            string.IsNullOrEmpty(action.ActionPath) &&
            string.Equals(action.Ref, "v4", StringComparison.Ordinal);
    }

    private static bool IsCheckoutAction(GitHubActionReference action)
    {
        return string.Equals(action.Owner, "actions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Repository, "checkout", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(action.ActionPath);
    }

    private static ActionPathResult ResolveLocalActionPath(string uses, string localReferenceRoot)
    {
        var fullLocalReferenceRoot = Path.GetFullPath(localReferenceRoot);
        var candidate = Path.GetFullPath(Path.Combine(fullLocalReferenceRoot, uses));
        if (!IsUnderRoot(candidate, fullLocalReferenceRoot))
        {
            return ActionPathResult.Failed([$"uses '{uses}' must stay inside the current action root."]);
        }

        if (File.Exists(candidate))
        {
            return IsActionFile(candidate)
                ? ActionPathResult.Resolved(candidate)
                : ActionPathResult.Failed([$"uses '{uses}' must point to an action.yml or action.yaml file."]);
        }

        if (Directory.Exists(candidate))
        {
            var ymlPath = Path.Combine(candidate, "action.yml");
            if (File.Exists(ymlPath))
            {
                return ActionPathResult.Resolved(ymlPath);
            }

            var yamlPath = Path.Combine(candidate, "action.yaml");
            if (File.Exists(yamlPath))
            {
                return ActionPathResult.Resolved(yamlPath);
            }
        }

        return ActionPathResult.Failed([$"uses '{uses}' could not be resolved to a local action.yml or action.yaml file."]);
    }

    private async Task<CompositeActionPlanResult> BuildCompositeStepsAsync(
        ActionDocument action,
        IReadOnlyDictionary<string, string> inputs,
        string projectRoot,
        string actionDirectory,
        IReadOnlyList<ActionResolutionFrame> stack,
        CancellationToken cancellationToken)
    {
        var steps = new List<CompositeActionStepPlan>();
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var step in action.Steps)
        {
            if (step.Run is not null)
            {
                var interpolation = ActionInputBinder.InterpolateInputExpressions(step.Run, inputs, projectRoot);
                if (interpolation.Success)
                {
                    steps.Add(CompositeActionStepPlan.RunStep(
                        step.Name,
                        interpolation.Value,
                        step.Id,
                        step.Shell,
                        step.WorkingDirectory));
                }
                else
                {
                    errors.AddRange(interpolation.Errors);
                }

                continue;
            }

            var with = InterpolateWith(inputs, step, projectRoot);
            if (!with.Success)
            {
                errors.AddRange(with.Errors);
                continue;
            }

            var nestedAction = await ResolveAsync(
                step.Uses,
                with.Values,
                projectRoot,
                actionDirectory,
                stack,
                cancellationToken);
            if (nestedAction.Success)
            {
                steps.Add(CompositeActionStepPlan.UsesStep(
                    step.Name,
                    step.Uses!,
                    step.Id,
                    with.Values,
                    nestedAction));
            }
            else
            {
                errors.AddRange(nestedAction.Errors);
            }
        }

        return errors.Count == 0
            ? CompositeActionPlanResult.Resolved(steps, warnings)
            : CompositeActionPlanResult.Failed(errors);
    }

    private static ActionInputMapInterpolationResult InterpolateWith(
        IReadOnlyDictionary<string, string> inputs,
        ActionStep step,
        string projectRoot)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var item in step.With)
        {
            var interpolation = ActionInputBinder.InterpolateInputExpressions(item.Value, inputs, projectRoot);
            if (interpolation.Success)
            {
                values[item.Key] = interpolation.Value;
            }
            else
            {
                errors.AddRange(interpolation.Errors.Select(error => $"action.runs.steps.{step.Name}.with.{item.Key}: {error}"));
            }
        }

        return errors.Count == 0
            ? ActionInputMapInterpolationResult.Resolved(values)
            : ActionInputMapInterpolationResult.Failed(errors);
    }

    private async Task<ActionResolutionResult> ResolveParsedActionAsync(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        ActionInputBindingResult inputBinding,
        string actionDirectory,
        string projectRoot,
        IReadOnlyList<ActionResolutionFrame> stack,
        IReadOnlyList<string> inheritedWarnings,
        CancellationToken cancellationToken)
    {
        var environment = MergeEnvironment(
            inputBinding.Environment,
            CreateActionPathEnvironment());
        var actionMount = new StepExecutionMount(actionDirectory, ActionContainerPath, ReadOnly: true);

        if (string.Equals(action.Runtime, ActionRuntime.Node20, StringComparison.Ordinal))
        {
            return AddWarnings(ActionResolutionResult.ResolvedJavaScriptAction(
                action,
                cacheEntry,
                FormatJavaScriptCommand(action.Main!),
                ActionContainerPath,
                action.Main!,
                action.Pre,
                action.Post,
                environment,
                [actionMount]),
                inheritedWarnings);
        }

        var compositePlan = await BuildCompositeStepsAsync(
            action,
            inputBinding.Inputs,
            projectRoot,
            actionDirectory,
            stack,
            cancellationToken);
        if (!compositePlan.Success)
        {
            return ActionResolutionResult.Failed(compositePlan.Errors);
        }

        return CreateResolvedCompositeAction(
            action,
            cacheEntry,
            inputBinding,
            actionDirectory,
            compositePlan,
            inheritedWarnings);
    }

    private static ActionResolutionResult CreateResolvedCompositeAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        ActionInputBindingResult inputBinding,
        string actionDirectory,
        CompositeActionPlanResult compositePlan,
        IReadOnlyList<string> inheritedWarnings)
    {
        var environment = MergeEnvironment(
            inputBinding.Environment,
            CreateActionPathEnvironment());
        var actionMount = new StepExecutionMount(actionDirectory, ActionContainerPath, ReadOnly: true);

        return AddWarnings(ActionResolutionResult.ResolvedCompositeAction(
            action,
            cacheEntry,
            compositePlan.Command,
            compositePlan.Steps,
            inputBinding.Inputs,
            action.Outputs.ToDictionary(item => item.Key, item => item.Value.Value!, StringComparer.Ordinal),
            environment,
            [actionMount]),
            inheritedWarnings.Concat(compositePlan.Warnings).ToArray());
    }

    private async Task<ActionResolutionResult> ResolveDockerfileActionAsync(
        string uses,
        ActionDocument action,
        ActionInputBindingResult inputBinding,
        string actionDirectory,
        string projectRoot,
        string? pinnedIdentity,
        string? mutablePart,
        CancellationToken cancellationToken)
    {
        var dockerfilePathResult = ResolveDockerfilePath(actionDirectory, action.Image);
        if (!dockerfilePathResult.Success)
        {
            return ActionResolutionResult.Failed(dockerfilePathResult.Errors);
        }

        try
        {
            var contentHash = await ComputeDirectoryHashAsync(actionDirectory, cancellationToken);
            var cacheEntry = await _cache.GetOrAddDockerfileActionAsync(
                new DockerfileActionCacheRequest(
                    uses,
                    actionDirectory,
                    dockerfilePathResult.DockerfilePath!,
                    contentHash,
                    pinnedIdentity,
                    mutablePart),
                cancellationToken);
            var environment = MergeEnvironment(
                inputBinding.Environment,
                CreateActionPathEnvironment());
            var actionMount = new StepExecutionMount(actionDirectory, ActionContainerPath, ReadOnly: true);

            return ActionResolutionResult.ResolvedDockerfileAction(
                action,
                cacheEntry,
                uses,
                FormatDockerfileImage(cacheEntry.Key),
                actionDirectory,
                dockerfilePathResult.DockerfilePath!,
                environment,
                [actionMount]);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return ActionResolutionResult.Failed([StorageError.Format($"caching Dockerfile action '{uses}'", ex)]);
        }
    }

    private static IReadOnlyDictionary<string, string> CreateActionPathEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ACTIO_ACTION_PATH"] = ActionContainerPath,
            ["GITHUB_ACTION_PATH"] = ActionContainerPath
        };
    }

    private static string FormatJavaScriptCommand(string main)
        => $"node {ToActionContainerPath(main)}";

    private static string ToActionContainerPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return $"{ActionContainerPath}/{normalized}";
    }

    private static string FormatDockerfileImage(string cacheKey)
        => $"{DockerfileImageRepository}:{cacheKey}";

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var environment = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var item in second)
        {
            environment[item.Key] = item.Value;
        }

        return environment;
    }

    private static async Task<string> ComputeContentHashAsync(
        string actionPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(actionPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeDirectoryHashAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var files = Directory
            .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directoryPath, path), StringComparer.Ordinal)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(directoryPath, filePath)
                .Replace('\\', '/');
            var relativePathBytes = Encoding.UTF8.GetBytes(relativePath);
            hash.AppendData(relativePathBytes);
            hash.AppendData([0]);

            await using var stream = File.OpenRead(filePath);
            var fileHash = await SHA256.HashDataAsync(stream, cancellationToken);
            hash.AppendData(fileHash);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsActionFile(string path)
    {
        return string.Equals(Path.GetFileName(path), "action.yml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(path), "action.yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);

        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static DockerfilePathResult ResolveDockerfilePath(string actionDirectory, string? image)
    {
        const string supportedImage = "Dockerfile";
        if (!string.Equals(image, supportedImage, StringComparison.Ordinal))
        {
            return DockerfilePathResult.Failed(["action.runs.image supports only 'Dockerfile' for Docker actions."]);
        }

        var fullActionDirectory = Path.GetFullPath(actionDirectory);
        var dockerfilePath = Path.GetFullPath(Path.Combine(fullActionDirectory, supportedImage));
        if (!IsUnderRoot(dockerfilePath, fullActionDirectory))
        {
            return DockerfilePathResult.Failed(["action.runs.image must stay inside the action directory."]);
        }

        return File.Exists(dockerfilePath)
            ? DockerfilePathResult.Resolved(dockerfilePath)
            : DockerfilePathResult.Failed(["action.runs.image points to Dockerfile, but no Dockerfile exists in the action directory."]);
    }

    private static ActionResolutionStackCheck CheckCycleAndDepth(
        string actionPath,
        IReadOnlyList<ActionResolutionFrame> stack)
    {
        var fullActionPath = Path.GetFullPath(actionPath);
        var cycleIndex = stack
            .Select((frame, index) => new { Frame = frame, Index = index })
            .FirstOrDefault(item => PathsEqual(item.Frame.ActionPath, fullActionPath));

        if (cycleIndex is not null)
        {
            var cycle = stack
                .Skip(cycleIndex.Index)
                .Select(frame => frame.Uses)
                .Concat([Path.GetFileName(Path.GetDirectoryName(fullActionPath)) ?? fullActionPath]);
            return ActionResolutionStackCheck.Failed([$"nested action cycle detected: {string.Join(" -> ", cycle)}."]);
        }

        if (stack.Count >= MaxNestedActionDepth)
        {
            return ActionResolutionStackCheck.Failed([$"nested action depth limit of {MaxNestedActionDepth} exceeded while resolving '{actionPath}'."]);
        }

        return ActionResolutionStackCheck.Resolved();
    }

    private static IReadOnlyList<ActionResolutionFrame> AddFrame(
        IReadOnlyList<ActionResolutionFrame> stack,
        string actionPath,
        string uses)
        => stack.Concat([new ActionResolutionFrame(Path.GetFullPath(actionPath), uses)]).ToArray();

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
    }

    private static string FormatMutableReferenceWarning(ActionReference reference)
    {
        return reference.Kind switch
        {
            ActionReferenceKind.DockerImage => $"nested uses '{reference.Value}' uses mutable Docker image reference ({reference.MutablePart}). Pin with an image digest such as docker://image@sha256:<digest> for safer reuse.",
            ActionReferenceKind.GitHubRepository => $"nested uses '{reference.Value}' uses mutable GitHub ref '{reference.MutablePart}'. Pin with a commit SHA for safer reuse.",
            _ => string.Empty
        };
    }

    private static ActionResolutionResult AddWarnings(
        ActionResolutionResult result,
        IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return result;
        }

        return result with
        {
            Warnings = result.Warnings.Concat(warnings).Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private sealed record ActionPathResult(
        bool Success,
        string? ActionPath,
        IReadOnlyList<string> Errors)
    {
        public static ActionPathResult Resolved(string actionPath)
            => new(true, actionPath, []);

        public static ActionPathResult Failed(IReadOnlyList<string> errors)
            => new(false, null, errors);
    }

    private sealed record ActionResolutionStackCheck(
        bool Success,
        IReadOnlyList<string> Errors)
    {
        public static ActionResolutionStackCheck Resolved()
            => new(true, []);

        public static ActionResolutionStackCheck Failed(IReadOnlyList<string> errors)
            => new(false, errors);
    }

    private sealed record DockerfilePathResult(
        bool Success,
        string? DockerfilePath,
        IReadOnlyList<string> Errors)
    {
        public static DockerfilePathResult Resolved(string dockerfilePath)
            => new(true, dockerfilePath, []);

        public static DockerfilePathResult Failed(IReadOnlyList<string> errors)
            => new(false, null, errors);
    }

    private sealed record DockerImageActionOptions(
        bool Success,
        string? EntryPoint,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<string> Errors)
    {
        public static DockerImageActionOptions FromInputs(IReadOnlyDictionary<string, string> inputs)
        {
            var entryPoint = inputs.TryGetValue("entrypoint", out var value)
                ? value
                : null;

            if (!inputs.TryGetValue("args", out var args))
            {
                return Resolved(entryPoint, []);
            }

            var arguments = SplitArguments(args);
            return arguments.Success
                ? Resolved(entryPoint, arguments.Arguments)
                : Failed(arguments.Errors);
        }

        private static DockerImageActionOptions Resolved(
            string? entryPoint,
            IReadOnlyList<string> arguments)
            => new(true, entryPoint, arguments, []);

        private static DockerImageActionOptions Failed(IReadOnlyList<string> errors)
            => new(false, null, [], errors);
    }

    private sealed record DockerImageActionArgumentResult(
        bool Success,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<string> Errors)
    {
        public static DockerImageActionArgumentResult Resolved(IReadOnlyList<string> arguments)
            => new(true, arguments, []);

        public static DockerImageActionArgumentResult Failed(IReadOnlyList<string> errors)
            => new(false, [], errors);
    }

    private static DockerImageActionArgumentResult SplitArguments(string value)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var tokenStarted = false;
        char? quote = null;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (quote is not null &&
                character == '\\' &&
                index + 1 < value.Length &&
                IsEscapedQuotedCharacter(value[index + 1], quote.Value))
            {
                current.Append(value[index + 1]);
                tokenStarted = true;
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                if (quote is null)
                {
                    quote = character;
                    tokenStarted = true;
                    continue;
                }

                if (quote == character)
                {
                    quote = null;
                    continue;
                }
            }

            if (quote is null && char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(character);
            tokenStarted = true;
        }

        if (quote is not null)
        {
            return DockerImageActionArgumentResult.Failed(["docker image action with.args contains an unterminated quote."]);
        }

        if (tokenStarted)
        {
            arguments.Add(current.ToString());
        }

        return DockerImageActionArgumentResult.Resolved(arguments);
    }

    private static bool IsEscapedQuotedCharacter(char character, char quote)
        => character == quote || character == '\\';
}

internal sealed record CompositeActionStepPlan(
    string Name,
    string? Command,
    string? Uses,
    string? Id,
    string? Shell,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> With,
    ActionResolutionResult? NestedAction)
{
    public bool IsNestedAction => NestedAction is not null;

    public static CompositeActionStepPlan RunStep(
        string name,
        string command,
        string? id,
        string? shell,
        string? workingDirectory)
    {
        return new CompositeActionStepPlan(
            name,
            command,
            null,
            id,
            shell,
            workingDirectory,
            new Dictionary<string, string>(),
            null);
    }

    public static CompositeActionStepPlan UsesStep(
        string name,
        string uses,
        string? id,
        IReadOnlyDictionary<string, string> with,
        ActionResolutionResult nestedAction)
    {
        return new CompositeActionStepPlan(
            name,
            null,
            uses,
            id,
            null,
            null,
            with,
            nestedAction);
    }
}

internal sealed record CompositeActionPlanResult(
    bool Success,
    IReadOnlyList<CompositeActionStepPlan> Steps,
    string Command,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public static CompositeActionPlanResult Resolved(
        IReadOnlyList<CompositeActionStepPlan> steps,
        IReadOnlyList<string> warnings)
    {
        return new CompositeActionPlanResult(
            true,
            steps,
            string.Join(Environment.NewLine, steps.Select(step => step.Command ?? step.Uses)),
            warnings,
            []);
    }

    public static CompositeActionPlanResult Failed(IReadOnlyList<string> errors)
        => new(false, [], string.Empty, [], errors);
}

internal sealed record ActionInputMapInterpolationResult(
    bool Success,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Errors)
{
    public static ActionInputMapInterpolationResult Resolved(IReadOnlyDictionary<string, string> values)
        => new(true, values, []);

    public static ActionInputMapInterpolationResult Failed(IReadOnlyList<string> errors)
        => new(false, new Dictionary<string, string>(), errors);
}

internal sealed record ActionResolutionFrame(
    string ActionPath,
    string Uses);

internal sealed record ActionResolutionResult(
    bool Success,
    ActionDocument? Action,
    ActionCacheEntry? CacheEntry,
    string? Command,
    IReadOnlyList<CompositeActionStepPlan> CompositeSteps,
    IReadOnlyDictionary<string, string> CompositeInputs,
    IReadOnlyDictionary<string, string> CompositeOutputExpressions,
    string? DockerImage,
    string? DockerEntryPoint,
    IReadOnlyList<string> DockerArguments,
    string? DockerfileBuildContext,
    string? DockerfilePath,
    string? JavaScriptActionPath,
    string? JavaScriptMain,
    string? JavaScriptPre,
    string? JavaScriptPost,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<StepExecutionMount> AdditionalMounts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool IsCompositeAction => CompositeSteps.Count > 0;

    public bool IsDockerImageAction => DockerImage is not null && DockerfileBuildContext is null;

    public bool IsDockerfileAction => DockerfileBuildContext is not null;

    public bool IsJavaScriptAction => JavaScriptMain is not null;

    public static ActionResolutionResult ResolvedCompositeAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command,
        IReadOnlyList<CompositeActionStepPlan> steps,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> outputExpressions,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> additionalMounts)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, steps, inputs, outputExpressions, null, null, [], null, null, null, null, null, null, environment, additionalMounts, [], []);
    }

    public static ActionResolutionResult ResolvedJavaScriptAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command,
        string actionPath,
        string main,
        string? pre,
        string? post,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> additionalMounts)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, [], new Dictionary<string, string>(), new Dictionary<string, string>(), null, null, [], null, null, actionPath, main, pre, post, environment, additionalMounts, [], []);
    }

    public static ActionResolutionResult ResolvedDockerfileAction(
        ActionDocument action,
        ActionCacheEntry cacheEntry,
        string command,
        string dockerImage,
        string buildContext,
        string dockerfilePath,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<StepExecutionMount> additionalMounts)
    {
        return new ActionResolutionResult(true, action, cacheEntry, command, [], new Dictionary<string, string>(), new Dictionary<string, string>(), dockerImage, null, [], buildContext, dockerfilePath, null, null, null, null, environment, additionalMounts, [], []);
    }

    public static ActionResolutionResult ResolvedBuiltInAction(string command)
    {
        return new ActionResolutionResult(true, null, null, command, [], new Dictionary<string, string>(), new Dictionary<string, string>(), null, null, [], null, null, null, null, null, null, new Dictionary<string, string>(), [], [], []);
    }

    public static ActionResolutionResult ResolvedDockerImage(
        string command,
        string dockerImage,
        ActionCacheEntry cacheEntry,
        IReadOnlyDictionary<string, string> environment,
        string? dockerEntryPoint,
        IReadOnlyList<string> dockerArguments)
    {
        return new ActionResolutionResult(
            true,
            null,
            cacheEntry,
            command,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            dockerImage,
            dockerEntryPoint,
            dockerArguments,
            null,
            null,
            null,
            null,
            null,
            null,
            environment,
            [],
            [],
            []);
    }

    public static ActionResolutionResult Failed(IReadOnlyList<string> errors)
    {
        return new ActionResolutionResult(false, null, null, null, [], new Dictionary<string, string>(), new Dictionary<string, string>(), null, null, [], null, null, null, null, null, null, new Dictionary<string, string>(), [], [], errors);
    }
}
