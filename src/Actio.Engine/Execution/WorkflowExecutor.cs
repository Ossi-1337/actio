using Actio.Core.Actions;
using Actio.Core.Expressions;
using Actio.Core.Workflows;
using Actio.Engine.Actions;
using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public sealed class WorkflowExecutor : IWorkflowExecutor
{
    private const string SuccessStatus = "Success";
    private const string FailedStatus = "Failed";
    private const string RunningStatus = "Running";
    private const string SkippedStatus = "Skipped";
    private const string TimedOutStatus = "TimedOut";

    private readonly IRunStore _runStore;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly JobExecutor _jobExecutor;

    public WorkflowExecutor(
        IRunnerProvider runnerProvider,
        IRunStore? runStore = null,
        IActionCache? actionCache = null,
        Func<int, TimeSpan>? createJobTimeout = null)
    {
        _runStore = runStore ?? new NullRunStore();
        var cache = actionCache ?? NullActionCache.Instance;
        var githubActionSourceProvider = cache as IGitHubActionSourceProvider ?? NullActionCache.Instance;
        var outputMarkerParser = new OutputMarkerParser();
        _conditionEvaluator = new ConditionEvaluator();
        var actionResolver = new ActionResolver(new ActionParser(), cache, githubActionSourceProvider);
        _jobExecutor = new JobExecutor(runnerProvider, _runStore, outputMarkerParser, actionResolver, createJobTimeout);
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDocument workflow,
        WorkflowExecutionOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var runId = options.RunId ?? _runStore.CreateRunId();
        var totalSteps = workflow.StepCount;
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
                runId: runId);
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
        var plan = JobGraphPlanner.Plan(workflow.Jobs);
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
                errors),
            cancellationToken);

        if (initialSaveError is not null)
        {
            return new WorkflowExecutionResult(
                WorkflowExecutionStatus.Failed,
                0,
                totalSteps,
                [initialSaveError],
                runId: runId,
                runRecordPath: null);
        }

        if (plan.Errors.Count > 0)
        {
            errors.AddRange(plan.Errors);
        }
        else
        {
            foreach (var job in plan.Jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outcome = await ExecuteOrSkipJobAsync(
                    job,
                    workflow.Env,
                    workflow.Defaults,
                    options.ProjectRoot,
                    options.RunTrigger.Inputs,
                    options.RunTrigger.EventPayload,
                    runId,
                    jobStatuses,
                    actualJobStatuses,
                    jobOutputs,
                    output,
                    error,
                    cancellationToken);

                successfulSteps += outcome.SuccessfulSteps;
                failedSteps += outcome.FailedSteps;
                skippedSteps += outcome.SkippedSteps;
                continuedSteps += outcome.ContinuedSteps;
                jobRecords.Add(outcome.Job);
                var toleratedFailure = job.ContinueOnError && IsUnsuccessfulJobStatus(outcome.Job.Status);
                jobStatuses[job.Name] = toleratedFailure ? SuccessStatus : outcome.Job.Status;
                actualJobStatuses[job.Name] = outcome.Job.Status;
                jobOutputs[job.Name] = outcome.Job.Outputs;
                errors.AddRange(
                    IsUnsuccessfulJobStatus(outcome.Job.Status) && !toleratedFailure
                        ? outcome.Job.Errors
                        : []);
                runArtifacts.AddRange(outcome.Job.Artifacts);
                runOutputs.AddRange(outcome.Job.Outputs.Select(item =>
                    new WorkflowRunOutput(job.Name, item.Key, item.Value)));

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
                        errors),
                    cancellationToken);

                if (progressSaveError is not null)
                {
                    errors.Add(progressSaveError);
                    break;
                }
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var status = errors.Count == 0 ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Failed;
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
            errors);
        var runRecordPath = storagePaths.RunRecordPath;

        var saveError = await TrySaveRunRecordAsync(runRecord, cancellationToken);
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
            continuedSteps);
    }

    private async Task<JobExecutionOutcome> ExecuteOrSkipJobAsync(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults workflowDefaults,
        string projectRoot,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
        string runId,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, string> actualJobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var skipReason = GetDependencySkipReason(job, jobStatuses);
        if (skipReason is null || CanRunAfterDependencyFailure(job.If))
        {
            var condition = _conditionEvaluator.EvaluateJob(
                job.If,
                jobOutputs,
                inputs,
                eventPayload,
                actualJobStatuses,
                job.Needs,
                projectRoot);

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

        return skipReason is null
            ? await _jobExecutor.ExecuteAsync(
                job,
                workflowEnv,
                workflowDefaults,
                jobOutputs,
                inputs,
                eventPayload,
                projectRoot,
                runId,
                output,
                error,
                cancellationToken)
            : CreateSkippedJobOutcome(job, skipReason);
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
        IReadOnlyList<string> errors)
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
            options.RunTrigger);
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
            JobExecutor.CreateSkippedStepRecords(job.Steps),
            [],
            [reason],
            job.Name,
            job.TimeoutMinutes,
            job.ContinueOnError,
            job.Concurrency?.Group,
            job.Concurrency?.CancelInProgress ?? false);

        return new JobExecutionOutcome(record, 0, 0, job.Steps.Count);
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
            JobExecutor.CreateSkippedStepRecords(job.Steps),
            [],
            [error],
            job.Name,
            job.TimeoutMinutes,
            job.ContinueOnError,
            job.Concurrency?.Group,
            job.Concurrency?.CancelInProgress ?? false);

        return new JobExecutionOutcome(record, 0, 0, job.Steps.Count);
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
}
