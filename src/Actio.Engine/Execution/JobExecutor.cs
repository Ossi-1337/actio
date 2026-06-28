using Actio.Core.Expressions;
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
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly Func<int, TimeSpan> _createTimeout;

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
        _conditionEvaluator = new ConditionEvaluator();
        _createTimeout = createJobTimeout ?? (minutes => TimeSpan.FromMinutes(minutes));
    }

    public async Task<JobExecutionOutcome> ExecuteAsync(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults workflowDefaults,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> inputs,
        WorkflowEventPayload eventPayload,
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
        var continuedSteps = 0;
        var errors = new List<string>();
        var stepRecords = new List<StepRunRecord>();
        var outputs = new Dictionary<string, string>(job.Outputs, StringComparer.Ordinal);
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var previousStepStatuses = new List<string>();
        var hardFailureSeen = false;
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
            return CompleteJob(job, FailedStatus, startedAt, outputs, stepRecords, artifacts, errors, 0, 0, job.Steps.Count, 0);
        }

        try
        {
            for (var index = 0; index < job.Steps.Count; index++)
            {
                currentStepIndex = index;
                var step = job.Steps[index];
                currentStep = step;
                jobCancellationToken.ThrowIfCancellationRequested();

                if (hardFailureSeen && !CanRunAfterHardFailure(step.If))
                {
                    output.WriteLine($"[{job.DisplayName}] {step.Name} (skipped)");
                    var skippedRecord = CreateSkippedStepRecord(step);
                    stepRecords.Add(skippedRecord);
                    skippedSteps++;
                    previousStepStatuses.Add(skippedRecord.Status);
                    continue;
                }

                var condition = _conditionEvaluator.EvaluateStep(
                    step.If,
                    jobOutputs,
                    inputs,
                    eventPayload,
                    previousStepStatuses);

                if (!condition.Success)
                {
                    var finishedAt = DateTimeOffset.UtcNow;
                    errors.Add($"workflow.jobs.{job.Name}.steps[{index}].if could not be evaluated: {condition.Error}");
                    var failedRecord = CreateFailedStepRecord(step, finishedAt);
                    stepRecords.Add(failedRecord);
                    failedSteps++;
                    hardFailureSeen = true;
                    previousStepStatuses.Add(failedRecord.Status);
                    continue;
                }

                if (!condition.ShouldRun)
                {
                    output.WriteLine($"[{job.DisplayName}] {step.Name} (skipped)");
                    var skippedRecord = CreateSkippedStepRecord(step);
                    stepRecords.Add(skippedRecord);
                    skippedSteps++;
                    previousStepStatuses.Add(skippedRecord.Status);
                    continue;
                }

                output.WriteLine($"[{job.DisplayName}] {step.Name}");

                var stepStartedAt = DateTimeOffset.UtcNow;
                currentStepStartedAt = stepStartedAt;
                var stepResult = await ExecuteStepAsync(
                    job,
                    step,
                    index,
                    workflowEnv,
                    runDefaults,
                    stepOutputs,
                    projectRoot,
                    runId,
                    output,
                    error,
                    jobCancellationToken);
                var stepFinishedAt = DateTimeOffset.UtcNow;

                outputs.Merge(stepResult.Outputs);
                if (step.Id is not null && stepResult.Outputs.Count > 0)
                {
                    stepOutputs[step.Id] = new Dictionary<string, string>(stepResult.Outputs, StringComparer.Ordinal);
                }

                var continuedFailure = step.ContinueOnError &&
                    stepResult.CountsAsFailedStep &&
                    IsFailureStatus(stepResult.Status);
                if (!continuedFailure)
                {
                    errors.AddRange(stepResult.Errors);
                }

                stepRecords.Add(new StepRunRecord(
                    step.Name,
                    stepResult.Status,
                    stepResult.Command,
                    stepResult.ExitCode,
                    stepResult.LogPath,
                    stepStartedAt,
                    stepFinishedAt,
                    ToDurationMilliseconds(stepStartedAt, stepFinishedAt),
                    step.Id,
                    stepResult.Shell,
                    stepResult.WorkingDirectory,
                    step.If,
                    step.TimeoutMinutes,
                    step.ContinueOnError));
                previousStepStatuses.Add(stepResult.Status);

                if (continuedFailure)
                {
                    continuedSteps++;
                    continue;
                }

                if (IsFailureStatus(stepResult.Status))
                {
                    failedSteps += stepResult.CountsAsFailedStep ? 1 : 0;
                    hardFailureSeen = true;
                    continue;
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
                var timedOutShell = currentStep.Run is null ? null : currentStep.Shell ?? runDefaults.Shell;
                var timedOutWorkingDirectory = currentStep.Run is null ? null : currentStep.WorkingDirectory ?? runDefaults.WorkingDirectory;

                stepRecords.Add(new StepRunRecord(
                    currentStep.Name,
                    TimedOutStatus,
                    currentStep.Run ?? currentStep.Uses ?? string.Empty,
                    null,
                    null,
                    currentStepStartedAt,
                    timedOutAt,
                    ToDurationMilliseconds(currentStepStartedAt ?? timedOutAt, timedOutAt),
                    currentStep.Id,
                    timedOutShell,
                    timedOutWorkingDirectory,
                    currentStep.If,
                    currentStep.TimeoutMinutes,
                    currentStep.ContinueOnError));
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
                skippedSteps,
                continuedSteps);
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
            skippedSteps,
            continuedSteps);
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
        timeoutTokenSource.CancelAfter(_createTimeout(job.TimeoutMinutes.Value));
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

    private CancellationTokenSource? CreateStepTimeoutTokenSource(
        WorkflowStep step,
        CancellationToken cancellationToken)
    {
        if (step.TimeoutMinutes is null)
        {
            return null;
        }

        var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutTokenSource.CancelAfter(_createTimeout(step.TimeoutMinutes.Value));
        return timeoutTokenSource;
    }

    private static bool IsStepTimeout(
        CancellationTokenSource? timeoutTokenSource,
        CancellationToken parentCancellationToken)
    {
        return timeoutTokenSource is not null &&
            timeoutTokenSource.IsCancellationRequested &&
            !parentCancellationToken.IsCancellationRequested;
    }

    private static string FormatStepTimeoutError(string jobName, string stepName, int timeoutMinutes)
    {
        return $"workflow.jobs.{jobName}.steps.{stepName} timed out after {timeoutMinutes} minute(s).";
    }

    private async Task<StepExecutionOutcome> ExecuteStepAsync(
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults runDefaults,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        string projectRoot,
        string runId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var timeoutTokenSource = CreateStepTimeoutTokenSource(step, cancellationToken);
        var stepCancellationToken = timeoutTokenSource?.Token ?? cancellationToken;
        IStepLog stepLog;

        try
        {
            stepLog = await _runStore.OpenStepLogAsync(runId, job.Name, stepIndex, step.Name, stepCancellationToken);
        }
        catch (OperationCanceledException) when (IsStepTimeout(timeoutTokenSource, cancellationToken))
        {
            return StepExecutionOutcome.TimedOut(
                step.Run ?? step.Uses ?? string.Empty,
                null,
                new Dictionary<string, string>(),
                null,
                null,
                FormatStepTimeoutError(job.Name, step.Name, step.TimeoutMinutes!.Value));
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(StorageError.Format($"opening log for job '{job.Name}' step '{step.Name}'", ex));
        }

        await using var collector = new StepOutputCollector(output, error, stepLog, _outputMarkerParser);
        var effectiveRunDefaults = runDefaults.Merge(new WorkflowRunDefaults(step.Shell, step.WorkingDirectory));
        StepExecutionPlan? plan = null;

        try
        {
            plan = await ResolveStepExecutionAsync(step, projectRoot, stepCancellationToken);
            if (!plan.Success)
            {
                return StepExecutionOutcome.FailedWithoutExitCode(step.Uses ?? string.Empty, plan.Errors, collector.LogPath);
            }

            var environment = CreateStepEnvironment(workflowEnv, job.Env, stepOutputs, step.Env, plan.Environment);
            var result = plan.Kind == StepExecutionKind.DockerImageAction
                ? await _runnerProvider.ExecuteDockerActionAsync(
                    new DockerActionExecutionRequest(
                        job.Name,
                        step.Name,
                        plan.DockerImage!,
                        projectRoot,
                        environment),
                    collector,
                    stepCancellationToken)
                : await _runnerProvider.ExecuteStepAsync(
                    new StepExecutionRequest(
                        job.Name,
                        step.Name,
                        job.RunsOn,
                        plan.Command!,
                        projectRoot,
                        environment,
                        effectiveRunDefaults.Shell,
                        effectiveRunDefaults.WorkingDirectory,
                        plan.AdditionalMounts),
                    collector,
                    stepCancellationToken);
            var resultShell = plan.Kind == StepExecutionKind.DockerImageAction ? null : effectiveRunDefaults.Shell;
            var resultWorkingDirectory = plan.Kind == StepExecutionKind.DockerImageAction ? null : effectiveRunDefaults.WorkingDirectory;

            if (!result.Success)
            {
                return StepExecutionOutcome.Failed(
                    plan.Command!,
                    result.ExitCode,
                    collector.LogPath,
                    collector.CapturedOutputs,
                    resultShell,
                    resultWorkingDirectory,
                    $"workflow.jobs.{job.Name}.steps.{step.Name} failed with exit code {result.ExitCode}.");
            }

            return StepExecutionOutcome.Succeeded(
                plan.Command!,
                result.ExitCode,
                collector.LogPath,
                collector.CapturedOutputs,
                resultShell,
                resultWorkingDirectory);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(
                StorageError.Format($"writing log for job '{job.Name}' step '{step.Name}'", ex),
                collector.LogPath);
        }
        catch (OperationCanceledException) when (IsStepTimeout(timeoutTokenSource, cancellationToken))
        {
            var usesShellExecution = plan?.Kind == StepExecutionKind.ShellCommand || step.Run is not null;
            return StepExecutionOutcome.TimedOut(
                step.Run ?? step.Uses ?? string.Empty,
                collector.LogPath,
                collector.CapturedOutputs,
                usesShellExecution ? effectiveRunDefaults.Shell : null,
                usesShellExecution ? effectiveRunDefaults.WorkingDirectory : null,
                FormatStepTimeoutError(job.Name, step.Name, step.TimeoutMinutes!.Value));
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
            ? StepExecutionPlan.DockerImageAction(action.Command!, action.DockerImage!, action.Environment)
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
        int skippedSteps,
        int continuedSteps)
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

        return new JobExecutionOutcome(record, successfulSteps, failedSteps, skippedSteps, continuedSteps);
    }

    public static IReadOnlyList<StepRunRecord> CreateSkippedStepRecords(IEnumerable<WorkflowStep> steps)
    {
        return steps
            .Select(CreateSkippedStepRecord)
            .ToArray();
    }

    private static StepRunRecord CreateSkippedStepRecord(WorkflowStep step)
    {
        return new StepRunRecord(
            step.Name,
            SkippedStatus,
            step.Run ?? step.Uses ?? string.Empty,
            null,
            null,
            null,
            null,
            0,
            step.Id,
            step.Shell,
            step.WorkingDirectory,
            step.If,
            step.TimeoutMinutes,
            step.ContinueOnError);
    }

    private static StepRunRecord CreateFailedStepRecord(WorkflowStep step, DateTimeOffset finishedAt)
    {
        return new StepRunRecord(
            step.Name,
            FailedStatus,
            step.Run ?? step.Uses ?? string.Empty,
            null,
            null,
            finishedAt,
            finishedAt,
            0,
            step.Id,
            step.Shell,
            step.WorkingDirectory,
            step.If,
            step.TimeoutMinutes,
            step.ContinueOnError);
    }

    private static bool IsFailureStatus(string status)
    {
        return string.Equals(status, FailedStatus, StringComparison.Ordinal) ||
            string.Equals(status, TimedOutStatus, StringComparison.Ordinal);
    }

    private static bool CanRunAfterHardFailure(string? expression)
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

    private static IReadOnlyDictionary<string, string> CreateStepEnvironment(
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, string> jobEnv,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> stepEnv,
        IReadOnlyDictionary<string, string> actionEnv)
    {
        var environment = new Dictionary<string, string>(workflowEnv, StringComparer.Ordinal);
        environment.Merge(jobEnv);
        environment.Merge(CreateStepOutputEnvironment(stepOutputs));
        environment.Merge(stepEnv);
        environment.Merge(actionEnv);
        return environment;
    }

    private static IReadOnlyDictionary<string, string> CreateStepOutputEnvironment(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var stepOutput in stepOutputs)
        {
            foreach (var output in stepOutput.Value)
            {
                environment[ToStepOutputEnvironmentName(stepOutput.Key, output.Key)] = output.Value;
            }
        }

        return environment;
    }

    private static string ToStepOutputEnvironmentName(string stepId, string outputName)
    {
        return $"ACTIO_STEP_{ToEnvironmentSegment(stepId)}_OUTPUT_{ToEnvironmentSegment(outputName)}";
    }

    private static string ToEnvironmentSegment(string value)
    {
        var characters = value
            .Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray();
        var segment = new string(characters);
        return string.IsNullOrEmpty(segment) ? "VALUE" : segment;
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
        string? Shell,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> Errors,
        bool CountsAsFailedStep)
    {
        public static StepExecutionOutcome Succeeded(
            string command,
            int exitCode,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string? shell,
            string? workingDirectory)
        {
            return new StepExecutionOutcome(SuccessStatus, command, exitCode, logPath, shell, workingDirectory, outputs, [], false);
        }

        public static StepExecutionOutcome Failed(
            string command,
            int exitCode,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string? shell,
            string? workingDirectory,
            string error)
        {
            return new StepExecutionOutcome(FailedStatus, command, exitCode, logPath, shell, workingDirectory, outputs, [error], true);
        }

        public static StepExecutionOutcome StorageFailed(string error, string? logPath = null)
        {
            return new StepExecutionOutcome(FailedStatus, string.Empty, null, logPath, null, null, new Dictionary<string, string>(), [error], false);
        }

        public static StepExecutionOutcome TimedOut(
            string command,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string? shell,
            string? workingDirectory,
            string error)
        {
            return new StepExecutionOutcome(TimedOutStatus, command, null, logPath, shell, workingDirectory, outputs, [error], true);
        }

        public static StepExecutionOutcome FailedWithoutExitCode(
            string command,
            IReadOnlyList<string> errors,
            string? logPath)
        {
            return new StepExecutionOutcome(FailedStatus, command, null, logPath, null, null, new Dictionary<string, string>(), errors, true);
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

        public static StepExecutionPlan DockerImageAction(
            string command,
            string dockerImage,
            IReadOnlyDictionary<string, string> environment)
        {
            return new(true, StepExecutionKind.DockerImageAction, command, dockerImage, environment, [], []);
        }

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
