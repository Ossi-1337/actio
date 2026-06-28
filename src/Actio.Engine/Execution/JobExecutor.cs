using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed class JobExecutor
{
    private const string SuccessStatus = "Success";
    private const string FailedStatus = "Failed";
    private const string SkippedStatus = "Skipped";
    private const string TimedOutStatus = "TimedOut";

    private readonly IRunnerProvider _runnerProvider;
    private readonly IRunStore _runStore;
    private readonly OutputMarkerParser _outputMarkerParser;
    private readonly ActionResolver _actionResolver;
    private readonly Func<int, TimeSpan> _createJobTimeout;

    public JobExecutor(
        IRunnerProvider runnerProvider,
        IRunStore runStore,
        OutputMarkerParser outputMarkerParser,
        ActionResolver actionResolver,
        Func<int, TimeSpan>? createJobTimeout = null)
    {
        _runnerProvider = runnerProvider;
        _runStore = runStore;
        _outputMarkerParser = outputMarkerParser;
        _actionResolver = actionResolver;
        _createJobTimeout = createJobTimeout ?? (minutes => TimeSpan.FromMinutes(minutes));
    }

    public async Task<JobExecutionOutcome> ExecuteAsync(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults workflowDefaults,
        string projectRoot,
        string runId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var successfulSteps = 0;
        var failedSteps = 0;
        var skippedSteps = 0;
        var errors = new List<string>();
        var stepRecords = new List<StepRunRecord>();
        var outputs = new Dictionary<string, string>(job.Outputs, StringComparer.Ordinal);
        var artifacts = new List<WorkflowRunArtifact>();
        var runDefaults = workflowDefaults.Merge(job.Defaults);
        using var timeoutTokenSource = CreateTimeoutTokenSource(job, cancellationToken);
        var jobCancellationToken = timeoutTokenSource?.Token ?? cancellationToken;
        var currentStepIndex = -1;
        WorkflowStep? currentStep = null;
        DateTimeOffset? currentStepStartedAt = null;

        if (!_runnerProvider.SupportsRunner(job.RunsOn))
        {
            errors.Add($"workflow.jobs.{job.Name}.runs-on '{job.RunsOn}' is not supported by the configured runner provider.");
            stepRecords.AddRange(CreateSkippedStepRecords(job.Steps));
            return CompleteJob(job, FailedStatus, startedAt, outputs, stepRecords, artifacts, errors, 0, 0, job.Steps.Count);
        }

        try
        {
            for (var index = 0; index < job.Steps.Count; index++)
            {
                currentStepIndex = index;
                var step = job.Steps[index];
                currentStep = step;
                jobCancellationToken.ThrowIfCancellationRequested();
                output.WriteLine($"[{job.DisplayName}] {step.Name}");

                var stepStartedAt = DateTimeOffset.UtcNow;
                currentStepStartedAt = stepStartedAt;
                var stepResult = await ExecuteStepAsync(
                    job,
                    step,
                    index,
                    workflowEnv,
                    runDefaults,
                    projectRoot,
                    runId,
                    output,
                    error,
                    jobCancellationToken);
                var stepFinishedAt = DateTimeOffset.UtcNow;

                outputs.Merge(stepResult.Outputs);
                errors.AddRange(stepResult.Errors);
                stepRecords.Add(new StepRunRecord(
                    step.Name,
                    stepResult.Status,
                    stepResult.Command,
                    stepResult.ExitCode,
                    stepResult.LogPath,
                    stepStartedAt,
                    stepFinishedAt,
                    ToDurationMilliseconds(stepStartedAt, stepFinishedAt)));

                if (stepResult.Status == FailedStatus)
                {
                    failedSteps += stepResult.CountsAsFailedStep ? 1 : 0;
                    var remainingSteps = job.Steps.Skip(index + 1).ToArray();
                    stepRecords.AddRange(CreateSkippedStepRecords(remainingSteps));
                    skippedSteps += remainingSteps.Length;
                    break;
                }

                successfulSteps++;
            }

            if (errors.Count == 0)
            {
                try
                {
                    var artifactResult = await _runStore.SaveArtifactsAsync(
                        runId,
                        job.Name,
                        projectRoot,
                        job.Artifacts,
                        jobCancellationToken);

                    artifacts.AddRange(artifactResult.Artifacts);
                    errors.AddRange(artifactResult.Errors);
                }
                catch (Exception ex) when (StorageError.IsRecoverable(ex))
                {
                    errors.Add(StorageError.Format($"saving artifacts for job '{job.Name}'", ex));
                }
            }
        }
        catch (OperationCanceledException) when (IsJobTimeout(timeoutTokenSource, cancellationToken))
        {
            var timedOutAt = DateTimeOffset.UtcNow;
            errors.Add($"workflow.jobs.{job.Name} timed out after {job.TimeoutMinutes} minute(s).");

            if (currentStep is not null && stepRecords.Count == currentStepIndex)
            {
                stepRecords.Add(new StepRunRecord(
                    currentStep.Name,
                    TimedOutStatus,
                    currentStep.Run ?? currentStep.Uses ?? string.Empty,
                    null,
                    null,
                    currentStepStartedAt,
                    timedOutAt,
                    ToDurationMilliseconds(currentStepStartedAt ?? timedOutAt, timedOutAt)));
                failedSteps++;
            }

            var remainingStepStart = currentStepIndex < 0 ? 0 : currentStepIndex + 1;
            var remainingSteps = job.Steps.Skip(remainingStepStart).ToArray();
            stepRecords.AddRange(CreateSkippedStepRecords(remainingSteps));
            skippedSteps += remainingSteps.Length;

            return CompleteJob(
                job,
                TimedOutStatus,
                startedAt,
                outputs,
                stepRecords,
                artifacts,
                errors,
                successfulSteps,
                failedSteps,
                skippedSteps);
        }

        return CompleteJob(
            job,
            errors.Count == 0 ? SuccessStatus : FailedStatus,
            startedAt,
            outputs,
            stepRecords,
            artifacts,
            errors,
            successfulSteps,
            failedSteps,
            skippedSteps);
    }

    private CancellationTokenSource? CreateTimeoutTokenSource(
        WorkflowJob job,
        CancellationToken cancellationToken)
    {
        if (job.TimeoutMinutes is null)
        {
            return null;
        }

        var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutTokenSource.CancelAfter(_createJobTimeout(job.TimeoutMinutes.Value));
        return timeoutTokenSource;
    }

    private static bool IsJobTimeout(
        CancellationTokenSource? timeoutTokenSource,
        CancellationToken externalCancellationToken)
    {
        return timeoutTokenSource is not null &&
            timeoutTokenSource.IsCancellationRequested &&
            !externalCancellationToken.IsCancellationRequested;
    }

    private async Task<StepExecutionOutcome> ExecuteStepAsync(
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults runDefaults,
        string projectRoot,
        string runId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IStepLog stepLog;

        try
        {
            stepLog = await _runStore.OpenStepLogAsync(runId, job.Name, stepIndex, step.Name, cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(StorageError.Format($"opening log for job '{job.Name}' step '{step.Name}'", ex));
        }

        await using var collector = new StepOutputCollector(output, error, stepLog, _outputMarkerParser);

        try
        {
            var plan = await ResolveStepExecutionAsync(step, projectRoot, cancellationToken);
            if (!plan.Success)
            {
                return StepExecutionOutcome.FailedWithoutExitCode(step.Uses ?? string.Empty, plan.Errors, collector.LogPath);
            }

            var environment = CreateStepEnvironment(workflowEnv, job.Env, plan.Environment);
            var result = plan.Kind == StepExecutionKind.DockerImageAction
                ? await _runnerProvider.ExecuteDockerActionAsync(
                    new DockerActionExecutionRequest(
                        job.Name,
                        step.Name,
                        plan.DockerImage!,
                        projectRoot,
                        environment),
                    collector,
                    cancellationToken)
                : await _runnerProvider.ExecuteStepAsync(
                    new StepExecutionRequest(
                        job.Name,
                        step.Name,
                        job.RunsOn,
                        plan.Command!,
                        projectRoot,
                        environment,
                        runDefaults.Shell,
                        runDefaults.WorkingDirectory,
                        plan.AdditionalMounts),
                    collector,
                    cancellationToken);

            if (!result.Success)
            {
                return StepExecutionOutcome.Failed(
                    plan.Command!,
                    result.ExitCode,
                    collector.LogPath,
                    collector.CapturedOutputs,
                    $"workflow.jobs.{job.Name}.steps.{step.Name} failed with exit code {result.ExitCode}.");
            }

            return StepExecutionOutcome.Succeeded(plan.Command!, result.ExitCode, collector.LogPath, collector.CapturedOutputs);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(
                StorageError.Format($"writing log for job '{job.Name}' step '{step.Name}'", ex),
                collector.LogPath);
        }
    }

    private async Task<StepExecutionPlan> ResolveStepExecutionAsync(
        WorkflowStep step,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        if (step.Run is not null)
        {
            return StepExecutionPlan.ShellCommand(step.Run);
        }

        var action = await _actionResolver.ResolveAsync(step, projectRoot, cancellationToken);
        if (!action.Success)
        {
            return StepExecutionPlan.Failed(action.Errors);
        }

        return action.IsDockerImageAction
            ? StepExecutionPlan.DockerImageAction(action.Command!, action.DockerImage!)
            : StepExecutionPlan.ShellCommand(action.Command!, action.Environment, action.AdditionalMounts);
    }

    private static JobExecutionOutcome CompleteJob(
        WorkflowJob job,
        string status,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<StepRunRecord> stepRecords,
        IReadOnlyList<WorkflowRunArtifact> artifacts,
        IReadOnlyList<string> errors,
        int successfulSteps,
        int failedSteps,
        int skippedSteps)
    {
        var finishedAt = DateTimeOffset.UtcNow;
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
            stepRecords,
            artifacts,
            errors,
            job.Name,
            job.TimeoutMinutes,
            job.ContinueOnError,
            job.Concurrency?.Group,
            job.Concurrency?.CancelInProgress ?? false);

        return new JobExecutionOutcome(record, successfulSteps, failedSteps, skippedSteps);
    }

    public static IReadOnlyList<StepRunRecord> CreateSkippedStepRecords(IEnumerable<WorkflowStep> steps)
    {
        return steps
            .Select(step => new StepRunRecord(step.Name, SkippedStatus, step.Run ?? step.Uses ?? string.Empty, null, null, null, null, 0))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> CreateStepEnvironment(
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, string> jobEnv,
        IReadOnlyDictionary<string, string> actionEnv)
    {
        var environment = new Dictionary<string, string>(workflowEnv, StringComparer.Ordinal);
        environment.Merge(jobEnv);
        environment.Merge(actionEnv);
        return environment;
    }

    private static long ToDurationMilliseconds(DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        return Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds);
    }

    private sealed record StepExecutionOutcome(
        string Status,
        string Command,
        int? ExitCode,
        string? LogPath,
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> Errors,
        bool CountsAsFailedStep)
    {
        public static StepExecutionOutcome Succeeded(
            string command,
            int exitCode,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs)
        {
            return new StepExecutionOutcome(SuccessStatus, command, exitCode, logPath, outputs, [], false);
        }

        public static StepExecutionOutcome Failed(
            string command,
            int exitCode,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string error)
        {
            return new StepExecutionOutcome(FailedStatus, command, exitCode, logPath, outputs, [error], true);
        }

        public static StepExecutionOutcome StorageFailed(string error, string? logPath = null)
        {
            return new StepExecutionOutcome(FailedStatus, string.Empty, null, logPath, new Dictionary<string, string>(), [error], false);
        }

        public static StepExecutionOutcome FailedWithoutExitCode(
            string command,
            IReadOnlyList<string> errors,
            string? logPath)
        {
            return new StepExecutionOutcome(FailedStatus, command, null, logPath, new Dictionary<string, string>(), errors, true);
        }
    }

    private enum StepExecutionKind
    {
        ShellCommand,
        DockerImageAction
    }

    private sealed record StepExecutionPlan(
        bool Success,
        StepExecutionKind Kind,
        string? Command,
        string? DockerImage,
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<StepExecutionMount> AdditionalMounts,
        IReadOnlyList<string> Errors)
    {
        public static StepExecutionPlan ShellCommand(
            string command,
            IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyList<StepExecutionMount>? additionalMounts = null)
        {
            return new(
                true,
                StepExecutionKind.ShellCommand,
                command,
                null,
                environment ?? new Dictionary<string, string>(),
                additionalMounts ?? [],
                []);
        }

        public static StepExecutionPlan DockerImageAction(string command, string dockerImage)
            => new(true, StepExecutionKind.DockerImageAction, command, dockerImage, new Dictionary<string, string>(), [], []);

        public static StepExecutionPlan Failed(IReadOnlyList<string> errors)
            => new(false, StepExecutionKind.ShellCommand, null, null, new Dictionary<string, string>(), [], errors);
    }
}

file static class DictionaryExtensions
{
    public static void Merge(this Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var item in source)
        {
            target[item.Key] = item.Value;
        }
    }
}
