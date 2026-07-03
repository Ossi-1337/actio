using Actio.Core.Actions;
using Actio.Core.Expressions;
using Actio.Core.Security;
using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Caching;
using Actio.Engine.Runs;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Actio.Engine.Execution;

public sealed class WorkflowExecutor : IWorkflowExecutor
{
    private const string SuccessStatus = "Success";
    private const string FailedStatus = "Failed";
    private const string RunningStatus = "Running";
    private const string SkippedStatus = "Skipped";
    private const string TimedOutStatus = "TimedOut";
    private const string CancelledStatus = "Cancelled";
    private const int MaxReusableWorkflowDepth = 10;

    private readonly IRunnerProvider _runnerProvider;
    private readonly IRunStore _runStore;
    private readonly IActionCache _actionCache;
    private readonly IDependencyCache _dependencyCache;
    private readonly Func<int, TimeSpan>? _createJobTimeout;
    private readonly WorkflowParser _workflowParser;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly JobExecutor _jobExecutor;

    public WorkflowExecutor(
        IRunnerProvider runnerProvider,
        IRunStore? runStore = null,
        IActionCache? actionCache = null,
        IDependencyCache? dependencyCache = null,
        Func<int, TimeSpan>? createJobTimeout = null)
    {
        _runStore = runStore ?? new NullRunStore();
        _runnerProvider = runnerProvider;
        var cache = actionCache ?? NullActionCache.Instance;
        _actionCache = cache;
        _dependencyCache = dependencyCache ?? NullDependencyCache.Instance;
        _createJobTimeout = createJobTimeout;
        _workflowParser = new WorkflowParser();
        var githubActionSourceProvider = cache as IGitHubActionSourceProvider ?? NullActionCache.Instance;
        var outputMarkerParser = new OutputMarkerParser();
        _conditionEvaluator = new ConditionEvaluator();
        var actionResolver = new ActionResolver(new ActionParser(), cache, githubActionSourceProvider);
        _jobExecutor = new JobExecutor(runnerProvider, _runStore, outputMarkerParser, actionResolver, _dependencyCache, createJobTimeout);
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDocument workflow,
        WorkflowExecutionOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var synchronizedOutput = TextWriter.Synchronized(output);
        var synchronizedError = TextWriter.Synchronized(error);
        var runId = options.RunId ?? _runStore.CreateRunId();
        var expansion = MatrixJobExpander.Expand(workflow.Jobs);
        var totalSteps = expansion.Jobs.Values.Sum(job => job.ExecutionStepCount);
        var securityFindings = WorkflowSecurityPolicy.Analyze(workflow);
        RunStoragePaths storagePaths;

        try
        {
            storagePaths = await _runStore.InitializeRunAsync(runId, cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return new WorkflowExecutionResult(
                WorkflowExecutionStatus.Failed,
                0,
                totalSteps,
                [StorageError.Format("initializing run storage", ex)],
                runId: runId,
                securityFindings: securityFindings);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var successfulSteps = 0;
        var failedSteps = 0;
        var skippedSteps = 0;
        var continuedSteps = 0;
        var errors = new List<string>();
        var jobRecords = new List<JobRunRecord>();
        var runOutputs = new List<WorkflowRunOutput>();
        var runArtifacts = new List<WorkflowRunArtifact>();
        var jobStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var actualJobStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var jobOutputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var plan = JobGraphPlanner.Plan(expansion.Jobs);
        var initialSaveError = await TrySaveRunRecordAsync(
            CreateRunRecord(
                runId,
                workflow,
                options,
                RunningStatus,
                startedAt,
                DateTimeOffset.UtcNow,
                jobRecords,
                runOutputs,
                runArtifacts,
                errors,
                securityFindings),
            cancellationToken);

        if (initialSaveError is not null)
        {
            return new WorkflowExecutionResult(
                WorkflowExecutionStatus.Failed,
                0,
                totalSteps,
                [initialSaveError],
                runId: runId,
                runRecordPath: null,
                securityFindings: securityFindings);
        }

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var runCancellationWatcher = RunCancellationWatcher.Start(_runStore, runId, executionCancellation);
        var executionToken = executionCancellation.Token;
        var cancelled = false;

        if (expansion.Errors.Count > 0)
        {
            errors.AddRange(expansion.Errors);
        }
        else if (plan.Errors.Count > 0)
        {
            errors.AddRange(plan.Errors);
        }
        else
        {
            var stopExecution = false;
            try
            {
                for (var index = 0; index < plan.Jobs.Count;)
                {
                    executionToken.ThrowIfCancellationRequested();
                    var jobGroup = GetMatrixJobGroup(plan.Jobs, index);
                    var outcomes = jobGroup.Count == 1
                        ? [new PlannedJobOutcome(
                            jobGroup[0],
                            await ExecuteOrSkipJobAsync(
                                workflow,
                                jobGroup[0],
                                options,
                                runId,
                                jobStatuses,
                                actualJobStatuses,
                                jobOutputs,
                                expansion.JobNamesByBaseName,
                                runArtifacts,
                                synchronizedOutput,
                                synchronizedError,
                                executionToken))]
                        : await ExecuteMatrixJobGroupAsync(
                            workflow,
                            jobGroup,
                            options,
                            runId,
                            jobStatuses,
                            actualJobStatuses,
                            jobOutputs,
                            expansion.JobNamesByBaseName,
                            runArtifacts,
                            synchronizedOutput,
                            synchronizedError,
                            executionToken);

                    foreach (var outcome in outcomes)
                    {
                        successfulSteps += outcome.Outcome.SuccessfulSteps;
                        failedSteps += outcome.Outcome.FailedSteps;
                        skippedSteps += outcome.Outcome.SkippedSteps;
                        continuedSteps += outcome.Outcome.ContinuedSteps;
                        jobRecords.Add(outcome.Outcome.Job);
                        var toleratedFailure = outcome.Job.ContinueOnError && IsUnsuccessfulJobStatus(outcome.Outcome.Job.Status);
                        jobStatuses[outcome.Job.Name] = toleratedFailure ? SuccessStatus : outcome.Outcome.Job.Status;
                        actualJobStatuses[outcome.Job.Name] = outcome.Outcome.Job.Status;
                        jobOutputs[outcome.Job.Name] = outcome.Outcome.Job.Outputs;
                        errors.AddRange(
                            IsUnsuccessfulJobStatus(outcome.Outcome.Job.Status) && !toleratedFailure
                                ? outcome.Outcome.Job.Errors
                                : []);
                        AddArtifactsOrDuplicateErrors(runArtifacts, outcome.Outcome.Job.Artifacts, errors);
                        runOutputs.AddRange(outcome.Outcome.Job.Outputs.Select(item =>
                            new WorkflowRunOutput(outcome.Job.Name, item.Key, item.Value)));

                        var progressSaveError = await TrySaveRunRecordAsync(
                            CreateRunRecord(
                                runId,
                                workflow,
                                options,
                                RunningStatus,
                                startedAt,
                                DateTimeOffset.UtcNow,
                                jobRecords,
                                runOutputs,
                                runArtifacts,
                                errors,
                                securityFindings),
                            executionToken);

                        if (progressSaveError is not null)
                        {
                            errors.Add(progressSaveError);
                            stopExecution = true;
                            break;
                        }
                    }

                    if (stopExecution)
                    {
                        break;
                    }

                    index += jobGroup.Count;
                }
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
            {
                cancelled = true;
                errors.Add("Workflow run was cancelled.");
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var status = cancelled
            ? WorkflowExecutionStatus.Cancelled
            : errors.Count == 0 ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Failed;
        var runRecord = CreateRunRecord(
            runId,
            workflow,
            options,
            status.ToString(),
            startedAt,
            finishedAt,
            jobRecords,
            runOutputs,
            runArtifacts,
            errors,
            securityFindings);
        var runRecordPath = storagePaths.RunRecordPath;

        var saveError = await TrySaveRunRecordAsync(runRecord, CancellationToken.None);
        if (saveError is not null)
        {
            status = WorkflowExecutionStatus.Failed;
            errors.Add(saveError);
            runRecordPath = null;
        }

        return new WorkflowExecutionResult(
            status,
            successfulSteps,
            totalSteps,
            errors,
            runOutputs,
            runArtifacts,
            runId,
            runRecordPath,
            failedSteps,
            skippedSteps,
            continuedSteps,
            securityFindings);
    }

    private async Task<JobExecutionOutcome> ExecuteOrSkipJobAsync(
        WorkflowDocument workflow,
        WorkflowJob job,
        WorkflowExecutionOptions options,
        string runId,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, string> actualJobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> jobNamesByBaseName,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var skipReason = GetDependencySkipReason(job, jobStatuses);
        var contextJobOutputs = CreateContextJobOutputs(job, jobNamesByBaseName, jobOutputs);
        var contextJobStatuses = CreateContextJobStatuses(job, jobNamesByBaseName, actualJobStatuses);
        if (skipReason is null || CanRunAfterDependencyFailure(job.If))
        {
            var condition = _conditionEvaluator.EvaluateJob(
                job.If,
                ExecutionExpressionContexts.ForJob(
                    workflow,
                    job,
                    options,
                    runId,
                    MergeEnvironment(workflow.Env, job.Env),
                    options.Variables,
                    options.Secrets,
                    contextJobOutputs,
                    contextJobStatuses),
                contextJobStatuses,
                job.Needs);

            if (!condition.Success)
            {
                return CreateFailedSkippedJobOutcome(
                    job,
                    $"workflow.jobs.{job.Name}.if could not be evaluated: {condition.Error}");
            }
            else if (!condition.ShouldRun)
            {
                skipReason = "if condition evaluated to false.";
            }
            else
            {
                skipReason = null;
            }
        }

        if (skipReason is not null)
        {
            return CreateSkippedJobOutcome(job, skipReason);
        }

        if (job.Call is not null)
        {
            return await ExecuteReusableWorkflowCallAsync(
                job,
                options,
                runId,
                output,
                error,
                cancellationToken);
        }

        return await _jobExecutor.ExecuteAsync(
            workflow.Name,
            job,
            workflow.Env,
            workflow.Defaults,
            contextJobOutputs,
            contextJobStatuses,
            options.RunTrigger,
            options.Variables,
            options.Secrets,
            availableArtifacts,
            options.ProjectRoot,
            runId,
            output,
            error,
            cancellationToken);
    }

    private async Task<IReadOnlyList<PlannedJobOutcome>> ExecuteMatrixJobGroupAsync(
        WorkflowDocument workflow,
        IReadOnlyList<WorkflowJob> jobs,
        WorkflowExecutionOptions options,
        string runId,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, string> actualJobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> jobNamesByBaseName,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<PlannedJobOutcome>();
        var maxParallel = Math.Max(1, jobs[0].Strategy.MaxParallel ?? 1);
        var failFast = jobs[0].Strategy.FailFast;

        for (var index = 0; index < jobs.Count; index += maxParallel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = jobs.Skip(index).Take(maxParallel).ToArray();
            var batchOutcomes = await Task.WhenAll(batch.Select(async job =>
                new PlannedJobOutcome(
                    job,
                    await ExecuteOrSkipJobAsync(
                        workflow,
                        job,
                        options,
                        runId,
                        jobStatuses,
                        actualJobStatuses,
                        jobOutputs,
                        jobNamesByBaseName,
                        availableArtifacts,
                        output,
                        error,
                        cancellationToken))));

            outcomes.AddRange(batchOutcomes);

            if (!failFast || !batchOutcomes.Any(IsFailFastFailure))
            {
                continue;
            }

            var remainingJobs = jobs.Skip(index + batch.Length);
            outcomes.AddRange(remainingJobs.Select(job =>
                new PlannedJobOutcome(
                    job,
                    CreateSkippedJobOutcome(
                        job,
                        "Matrix fail-fast skipped this job because another matrix job failed."))));
            break;
        }

        return outcomes;
    }

    private async Task<JobExecutionOutcome> ExecuteReusableWorkflowCallAsync(
        WorkflowJob job,
        WorkflowExecutionOptions options,
        string runId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        output.WriteLine($"[{job.DisplayName}] Call reusable workflow");

        var pathResolution = ResolveReusableWorkflowPath(job, options);
        if (!pathResolution.Success)
        {
            return CreateReusableWorkflowCallOutcome(
                job,
                FailedStatus,
                startedAt,
                new Dictionary<string, string>(),
                pathResolution.Errors,
                successfulSteps: 0,
                failedSteps: 1);
        }

        var calleePath = pathResolution.Path!;
        var parseResult = _workflowParser.ParseFile(calleePath);
        if (!parseResult.Success)
        {
            return CreateReusableWorkflowCallOutcome(
                job,
                FailedStatus,
                startedAt,
                new Dictionary<string, string>(),
                PrefixReusableWorkflowErrors(job, calleePath, parseResult.Errors),
                successfulSteps: 0,
                failedSteps: 1);
        }

        var calleeWorkflow = parseResult.Workflow!;
        var workflowCall = calleeWorkflow.Triggers
            .FirstOrDefault(trigger => string.Equals(trigger.EventName, "workflow_call", StringComparison.Ordinal))
            ?.Call;
        if (workflowCall is null)
        {
            return CreateReusableWorkflowCallOutcome(
                job,
                FailedStatus,
                startedAt,
                new Dictionary<string, string>(),
                [$"{FormatReusableWorkflowLocation(job, calleePath)} does not declare on.workflow_call."],
                successfulSteps: 0,
                failedSteps: 1);
        }

        var binding = BindReusableWorkflowCall(job, workflowCall, calleePath, options);
        if (!binding.Success)
        {
            return CreateReusableWorkflowCallOutcome(
                job,
                FailedStatus,
                startedAt,
                new Dictionary<string, string>(),
                binding.Errors,
                successfulSteps: 0,
                failedSteps: 1);
        }

        var nestedExecutor = new WorkflowExecutor(
            _runnerProvider,
            new NullRunStore(),
            _actionCache,
            _dependencyCache,
            _createJobTimeout);
        var nestedOptions = new WorkflowExecutionOptions(
            options.ProjectRoot,
            calleePath,
            runId,
            new WorkflowRunTrigger(
                "workflow_call",
                $"workflow.jobs.{job.Name}",
                binding.Inputs),
            options.ReusableWorkflowCallStack.Append(calleePath).ToArray(),
            Secrets: binding.Secrets,
            Variables: options.Variables);
        var nestedResult = await nestedExecutor.ExecuteAsync(
            calleeWorkflow,
            nestedOptions,
            output,
            error,
            cancellationToken);
        var errors = new List<string>();

        if (!nestedResult.Success)
        {
            errors.AddRange(PrefixReusableWorkflowErrors(job, calleePath, nestedResult.Errors));
        }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (errors.Count == 0)
        {
            var outputResolution = ResolveReusableWorkflowOutputs(job, calleePath, workflowCall, nestedResult.Outputs, options.ProjectRoot);
            foreach (var outputValue in outputResolution.Outputs)
            {
                outputs[outputValue.Key] = outputValue.Value;
            }

            errors.AddRange(outputResolution.Errors);
        }

        var failed = errors.Count > 0;
        return CreateReusableWorkflowCallOutcome(
            job,
            failed ? FailedStatus : SuccessStatus,
            startedAt,
            outputs,
            errors,
            successfulSteps: failed ? 0 : 1,
            failedSteps: failed ? 1 : 0);
    }

    private static ReusableWorkflowPathResolution ResolveReusableWorkflowPath(
        WorkflowJob job,
        WorkflowExecutionOptions options)
    {
        var uses = job.Call!.Uses;
        var normalizedUses = uses.Replace('\\', '/');
        if (!normalizedUses.StartsWith("./", StringComparison.Ordinal))
        {
            return ReusableWorkflowPathResolution.Failed(
                [$"workflow.jobs.{job.Name}.uses supports only local reusable workflow references in this milestone."]);
        }

        if (!IsSupportedReusableWorkflowPath(normalizedUses))
        {
            return ReusableWorkflowPathResolution.Failed(
                [$"workflow.jobs.{job.Name}.uses '{uses}' must reference a workflow under .workflows/ or .github/workflows/ with a .yml or .yaml extension."]);
        }

        var fullPath = Path.GetFullPath(Path.Combine(options.ProjectRoot, normalizedUses[2..]));
        var projectRoot = Path.GetFullPath(options.ProjectRoot);
        var rootWithSeparator = projectRoot.EndsWith(Path.DirectorySeparatorChar)
            ? projectRoot
            : $"{projectRoot}{Path.DirectorySeparatorChar}";

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return ReusableWorkflowPathResolution.Failed(
                [$"workflow.jobs.{job.Name}.uses '{uses}' must stay inside the project root."]);
        }

        if (options.ReusableWorkflowCallStack.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            return ReusableWorkflowPathResolution.Failed(
                [$"workflow.jobs.{job.Name}.uses '{uses}' creates a reusable workflow call cycle."]);
        }

        if (options.ReusableWorkflowCallStack.Count >= MaxReusableWorkflowDepth)
        {
            return ReusableWorkflowPathResolution.Failed(
                [$"workflow.jobs.{job.Name}.uses '{uses}' exceeds the reusable workflow call depth limit of {MaxReusableWorkflowDepth}."]);
        }

        if (!File.Exists(fullPath))
        {
            return ReusableWorkflowPathResolution.Failed(
                [$"workflow.jobs.{job.Name}.uses '{uses}' was not found at '{fullPath}'."]);
        }

        return ReusableWorkflowPathResolution.Resolved(fullPath);
    }

    private static ReusableWorkflowCallBinding BindReusableWorkflowCall(
        WorkflowJob job,
        WorkflowCall workflowCall,
        string calleePath,
        WorkflowExecutionOptions options)
    {
        var errors = new List<string>();
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var expressionContext = ExecutionExpressionContexts.ForWorkflowCallValues(options);

        foreach (var input in workflowCall.Inputs.Values)
        {
            if (input.Default is not null)
            {
                inputs[input.Name] = input.Default;
            }
        }

        foreach (var input in job.Call!.With)
        {
            if (!workflowCall.Inputs.TryGetValue(input.Key, out var inputContract))
            {
                errors.Add($"{FormatReusableWorkflowLocation(job, calleePath)} does not declare workflow_call input '{input.Key}'.");
                continue;
            }

            var inputValue = InterpolateReusableWorkflowCallValue(
                errors,
                $"workflow.jobs.{job.Name}.with.{input.Key}",
                input.Value,
                expressionContext);
            if (inputValue is null)
            {
                continue;
            }

            ValidateReusableWorkflowInputValue(errors, job, calleePath, inputContract, inputValue);
            inputs[input.Key] = inputValue;
        }

        foreach (var input in workflowCall.Inputs.Values)
        {
            if (input.Required && !inputs.ContainsKey(input.Name))
            {
                errors.Add($"{FormatReusableWorkflowLocation(job, calleePath)} requires workflow_call input '{input.Name}', but workflow.jobs.{job.Name}.with.{input.Name} is not set.");
            }
        }

        foreach (var secret in workflowCall.Secrets.Values)
        {
            secrets[secret.Name] = string.Empty;
        }

        foreach (var secret in job.Call.Secrets)
        {
            if (!workflowCall.Secrets.ContainsKey(secret.Key))
            {
                errors.Add($"{FormatReusableWorkflowLocation(job, calleePath)} does not declare workflow_call secret '{secret.Key}'.");
                continue;
            }

            var secretValue = InterpolateReusableWorkflowCallValue(
                errors,
                $"workflow.jobs.{job.Name}.secrets.{secret.Key}",
                secret.Value,
                expressionContext);
            if (secretValue is null)
            {
                continue;
            }

            secrets[secret.Key] = secretValue;
        }

        foreach (var secret in workflowCall.Secrets.Values)
        {
            if (secret.Required && (!secrets.TryGetValue(secret.Name, out var value) || value.Length == 0))
            {
                errors.Add($"{FormatReusableWorkflowLocation(job, calleePath)} requires workflow_call secret '{secret.Name}', but workflow.jobs.{job.Name}.secrets.{secret.Name} is not set.");
            }
        }

        return errors.Count == 0
            ? ReusableWorkflowCallBinding.Resolved(inputs, secrets)
            : ReusableWorkflowCallBinding.Failed(errors);
    }

    private static string? InterpolateReusableWorkflowCallValue(
        List<string> errors,
        string path,
        string value,
        ExpressionContextData expressionContext)
    {
        var interpolation = ExpressionTemplate.Interpolate(
            value,
            new ExpressionEvaluationContext(
                expressionContext.Resolve,
                workspaceRoot: expressionContext.WorkspaceRoot));
        if (interpolation.Success)
        {
            return interpolation.Value;
        }

        foreach (var error in interpolation.Errors)
        {
            errors.Add($"{path} could not be evaluated: {error}");
        }

        return null;
    }

    private static bool IsSupportedReusableWorkflowPath(string normalizedUses)
    {
        var hasSupportedRoot =
            normalizedUses.StartsWith("./.workflows/", StringComparison.Ordinal) ||
            normalizedUses.StartsWith("./.github/workflows/", StringComparison.Ordinal);
        var hasSupportedExtension =
            normalizedUses.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            normalizedUses.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

        return hasSupportedRoot && hasSupportedExtension;
    }

    private static void ValidateReusableWorkflowInputValue(
        List<string> errors,
        WorkflowJob job,
        string calleePath,
        WorkflowCallInput input,
        string value)
    {
        if (string.Equals(input.Type, "boolean", StringComparison.Ordinal) &&
            !bool.TryParse(value, out _))
        {
            errors.Add($"{FormatReusableWorkflowLocation(job, calleePath)} input '{input.Name}' expects a boolean value.");
            return;
        }

        if (string.Equals(input.Type, "number", StringComparison.Ordinal) &&
            !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            errors.Add($"{FormatReusableWorkflowLocation(job, calleePath)} input '{input.Name}' expects a number value.");
        }
    }

    private static ReusableWorkflowOutputResolution ResolveReusableWorkflowOutputs(
        WorkflowJob job,
        string calleePath,
        WorkflowCall workflowCall,
        IReadOnlyList<WorkflowRunOutput> runOutputs,
        string projectRoot)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var context = new ExpressionContextData(
            [
                ExpressionContextRoot.AvailableRoot(
                    "jobs",
                    CreateReusableWorkflowJobsContext(runOutputs),
                    allowMissingProperties: true,
                    includeInSafeSnapshot: false)
            ],
            projectRoot);
        var evaluationContext = new ExpressionEvaluationContext(context.Resolve, workspaceRoot: projectRoot);

        foreach (var output in workflowCall.Outputs.Values)
        {
            var interpolation = ExpressionTemplate.Interpolate(output.Value, evaluationContext);
            if (interpolation.Success)
            {
                outputs[output.Name] = interpolation.Value;
                continue;
            }

            errors.AddRange(interpolation.Errors.Select(error =>
                $"{FormatReusableWorkflowLocation(job, calleePath)} output '{output.Name}' could not be evaluated: {error}"));
        }

        return new ReusableWorkflowOutputResolution(outputs, errors);
    }

    private static JsonObject CreateReusableWorkflowJobsContext(IReadOnlyList<WorkflowRunOutput> runOutputs)
    {
        var jobs = new JsonObject();
        foreach (var group in runOutputs.GroupBy(output => output.JobName, StringComparer.Ordinal))
        {
            jobs[group.Key] = new JsonObject
            {
                ["outputs"] = ExpressionContextData.FromStrings(
                    group.ToDictionary(output => output.Name, output => output.Value, StringComparer.Ordinal))
            };
        }

        return jobs;
    }

    private static JobExecutionOutcome CreateReusableWorkflowCallOutcome(
        WorkflowJob job,
        string status,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<string> errors,
        int successfulSteps,
        int failedSteps)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var step = new StepRunRecord(
            "Call reusable workflow",
            status,
            job.Call!.Uses,
            null,
            null,
            startedAt,
            finishedAt,
            ToDurationMilliseconds(startedAt, finishedAt));
        var record = new JobRunRecord(
            job.DisplayName,
            status,
            job.RunsOn,
            job.Needs,
            job.If,
            startedAt,
            finishedAt,
            ToDurationMilliseconds(startedAt, finishedAt),
            outputs,
            [step],
            [],
            errors,
            job.Name,
            job.TimeoutMinutes,
            job.ContinueOnError,
            job.Concurrency?.Group,
            job.Concurrency?.CancelInProgress ?? false,
            job.Matrix,
            job.Environment);

        return new JobExecutionOutcome(record, successfulSteps, failedSteps, 0);
    }

    private static IReadOnlyList<string> PrefixReusableWorkflowErrors(
        WorkflowJob job,
        string calleePath,
        IReadOnlyList<string> errors)
    {
        return errors
            .Select(error => $"{FormatReusableWorkflowLocation(job, calleePath)}: {error}")
            .ToArray();
    }

    private static string FormatReusableWorkflowLocation(WorkflowJob job, string calleePath)
    {
        return $"workflow.jobs.{job.Name}.uses '{job.Call!.Uses}' -> callee '{calleePath}'";
    }

    private static bool IsFailFastFailure(PlannedJobOutcome outcome)
    {
        return !outcome.Job.ContinueOnError &&
            IsUnsuccessfulJobStatus(outcome.Outcome.Job.Status);
    }

    private static void AddArtifactsOrDuplicateErrors(
        List<WorkflowRunArtifact> runArtifacts,
        IReadOnlyList<WorkflowRunArtifact> newArtifacts,
        List<string> errors)
    {
        var artifactNames = runArtifacts
            .Select(artifact => artifact.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var artifact in newArtifacts)
        {
            if (!artifactNames.Add(artifact.Name))
            {
                errors.Add($"artifact '{artifact.Name}' was saved more than once in this run.");
                continue;
            }

            runArtifacts.Add(artifact);
        }
    }

    private static WorkflowRunRecord CreateRunRecord(
        string runId,
        WorkflowDocument workflow,
        WorkflowExecutionOptions options,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        IReadOnlyList<JobRunRecord> jobRecords,
        IReadOnlyList<WorkflowRunOutput> runOutputs,
        IReadOnlyList<WorkflowRunArtifact> runArtifacts,
        IReadOnlyList<string> errors,
        IReadOnlyList<WorkflowSecurityFinding> securityFindings)
    {
        return new WorkflowRunRecord(
            runId,
            workflow.Name,
            options.WorkflowPath,
            options.ProjectRoot,
            status,
            startedAt,
            finishedAt,
            ToDurationMilliseconds(startedAt, finishedAt),
            jobRecords.ToArray(),
            runOutputs.ToArray(),
            runArtifacts.ToArray(),
            errors.ToArray(),
            workflow.Triggers,
            options.RunTrigger,
            securityFindings.ToArray());
    }

    private async Task<string?> TrySaveRunRecordAsync(
        WorkflowRunRecord runRecord,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runStore.SaveRunRecordAsync(runRecord, cancellationToken);
            return null;
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StorageError.Format("saving run record", ex);
        }
    }

    private static JobExecutionOutcome CreateSkippedJobOutcome(WorkflowJob job, string reason)
    {
        var record = new JobRunRecord(
            job.DisplayName,
            SkippedStatus,
            job.RunsOn,
            job.Needs,
            job.If,
            null,
            null,
            0,
            new Dictionary<string, string>(),
            CreateSkippedStepRecords(job),
            [],
            [reason],
            job.Name,
            job.TimeoutMinutes,
            job.ContinueOnError,
            job.Concurrency?.Group,
            job.Concurrency?.CancelInProgress ?? false,
            job.Matrix,
            job.Environment);

        return new JobExecutionOutcome(record, 0, 0, job.ExecutionStepCount);
    }

    private static JobExecutionOutcome CreateFailedSkippedJobOutcome(WorkflowJob job, string error)
    {
        var record = new JobRunRecord(
            job.DisplayName,
            FailedStatus,
            job.RunsOn,
            job.Needs,
            job.If,
            null,
            null,
            0,
            new Dictionary<string, string>(),
            CreateSkippedStepRecords(job),
            [],
            [error],
            job.Name,
            job.TimeoutMinutes,
            job.ContinueOnError,
            job.Concurrency?.Group,
            job.Concurrency?.CancelInProgress ?? false,
            job.Matrix,
            job.Environment);

        return new JobExecutionOutcome(record, 0, 0, job.ExecutionStepCount);
    }

    private static IReadOnlyList<StepRunRecord> CreateSkippedStepRecords(WorkflowJob job)
    {
        if (job.Call is null)
        {
            return JobExecutor.CreateSkippedStepRecords(job.Steps);
        }

        return [new StepRunRecord("Call reusable workflow", SkippedStatus, job.Call.Uses, null, null, null, null, 0)];
    }

    private static string? GetDependencySkipReason(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> jobStatuses)
    {
        foreach (var neededJob in job.Needs)
        {
            if (!jobStatuses.TryGetValue(neededJob, out var status) ||
                !string.Equals(status, SuccessStatus, StringComparison.Ordinal))
            {
                return $"Dependency '{neededJob}' did not complete successfully.";
            }
        }

        return null;
    }

    private static bool CanRunAfterDependencyFailure(string? expression)
    {
        if (expression is null)
        {
            return false;
        }

        var parseResult = ExpressionParser.ParseTemplateExpression(expression);
        return parseResult.Success &&
            ExpressionAnalysis
                .CollectFunctionCalls(parseResult.Expression!)
                .Any(function => ExpressionBuiltIns.IsStatusFunction(function.Name));
    }

    private static long ToDurationMilliseconds(DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        return Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds);
    }

    private static bool IsUnsuccessfulJobStatus(string status)
    {
        return string.Equals(status, FailedStatus, StringComparison.Ordinal) ||
            string.Equals(status, TimedOutStatus, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, string> jobEnv)
    {
        var environment = new Dictionary<string, string>(workflowEnv, StringComparer.Ordinal);
        foreach (var item in jobEnv)
        {
            environment[item.Key] = item.Value;
        }

        return environment;
    }

    private static IReadOnlyList<WorkflowJob> GetMatrixJobGroup(
        IReadOnlyList<WorkflowJob> jobs,
        int startIndex)
    {
        var first = jobs[startIndex];
        if (!IsMatrixJob(first))
        {
            return [first];
        }

        var group = new List<WorkflowJob> { first };
        for (var index = startIndex + 1; index < jobs.Count; index++)
        {
            var next = jobs[index];
            if (!IsMatrixJob(next) ||
                !string.Equals(next.BaseName, first.BaseName, StringComparison.Ordinal))
            {
                break;
            }

            group.Add(next);
        }

        return group;
    }

    private static bool IsMatrixJob(WorkflowJob job)
    {
        return job.Matrix.Count > 0;
    }

    private static IReadOnlyDictionary<string, string> CreateContextJobStatuses(
        WorkflowJob job,
        IReadOnlyDictionary<string, IReadOnlyList<string>> jobNamesByBaseName,
        IReadOnlyDictionary<string, string> jobStatuses)
    {
        var statuses = new Dictionary<string, string>(jobStatuses, StringComparer.Ordinal);
        foreach (var logicalNeed in job.LogicalNeeds)
        {
            statuses[logicalNeed] = AggregateLogicalStatus(logicalNeed, jobNamesByBaseName, jobStatuses);
        }

        return statuses;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> CreateContextJobOutputs(
        WorkflowJob job,
        IReadOnlyDictionary<string, IReadOnlyList<string>> jobNamesByBaseName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs)
    {
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(jobOutputs, StringComparer.Ordinal);
        foreach (var logicalNeed in job.LogicalNeeds)
        {
            outputs[logicalNeed] = AggregateLogicalOutputs(logicalNeed, jobNamesByBaseName, jobOutputs);
        }

        return outputs;
    }

    private static string AggregateLogicalStatus(
        string logicalNeed,
        IReadOnlyDictionary<string, IReadOnlyList<string>> jobNamesByBaseName,
        IReadOnlyDictionary<string, string> jobStatuses)
    {
        if (!jobNamesByBaseName.TryGetValue(logicalNeed, out var expandedNames))
        {
            return jobStatuses.TryGetValue(logicalNeed, out var status) ? status : SkippedStatus;
        }

        var statuses = expandedNames
            .Select(name => jobStatuses.TryGetValue(name, out var status) ? status : SkippedStatus)
            .ToArray();

        if (statuses.Any(IsUnsuccessfulJobStatus))
        {
            return FailedStatus;
        }

        if (statuses.Any(status => string.Equals(status, SkippedStatus, StringComparison.Ordinal)))
        {
            return SkippedStatus;
        }

        if (statuses.Any(status => string.Equals(status, RunningStatus, StringComparison.Ordinal)))
        {
            return RunningStatus;
        }

        return SuccessStatus;
    }

    private static IReadOnlyDictionary<string, string> AggregateLogicalOutputs(
        string logicalNeed,
        IReadOnlyDictionary<string, IReadOnlyList<string>> jobNamesByBaseName,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs)
    {
        if (!jobNamesByBaseName.TryGetValue(logicalNeed, out var expandedNames))
        {
            return jobOutputs.TryGetValue(logicalNeed, out var logicalOutputs)
                ? logicalOutputs
                : new Dictionary<string, string>();
        }

        var mergedOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expandedName in expandedNames)
        {
            if (!jobOutputs.TryGetValue(expandedName, out var expandedOutputs))
            {
                continue;
            }

            foreach (var output in expandedOutputs)
            {
                mergedOutputs[output.Key] = output.Value;
            }
        }

        return mergedOutputs;
    }

    private sealed record PlannedJobOutcome(
        WorkflowJob Job,
        JobExecutionOutcome Outcome);

    private sealed record ReusableWorkflowPathResolution(
        bool Success,
        string? Path,
        IReadOnlyList<string> Errors)
    {
        public static ReusableWorkflowPathResolution Resolved(string path)
            => new(true, path, []);

        public static ReusableWorkflowPathResolution Failed(IReadOnlyList<string> errors)
            => new(false, null, errors);
    }

    private sealed record ReusableWorkflowCallBinding(
        bool Success,
        IReadOnlyDictionary<string, string> Inputs,
        IReadOnlyDictionary<string, string> Secrets,
        IReadOnlyList<string> Errors)
    {
        public static ReusableWorkflowCallBinding Resolved(
            IReadOnlyDictionary<string, string> inputs,
            IReadOnlyDictionary<string, string> secrets)
        {
            return new ReusableWorkflowCallBinding(true, inputs, secrets, []);
        }

        public static ReusableWorkflowCallBinding Failed(IReadOnlyList<string> errors)
        {
            return new ReusableWorkflowCallBinding(
                false,
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                errors);
        }
    }

    private sealed record ReusableWorkflowOutputResolution(
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> Errors);
}
