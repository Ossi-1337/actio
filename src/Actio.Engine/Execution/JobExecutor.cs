using Actio.Core.Actions;
using Actio.Core.Expressions;
using Actio.Core.Workflows;
using Actio.Engine.Artifacts;
using Actio.Engine.Caching;
using Actio.Engine.Runs;
using Actio.Engine.Setup;

namespace Actio.Engine.Execution;

internal sealed class JobExecutor
{
    private const string StepEnvironmentFileContainerDirectory = "/actio/env";
    internal const string DefaultContainerPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    private const string SuccessStatus = "Success";
    private const string FailedStatus = "Failed";
    private const string SkippedStatus = "Skipped";
    private const string TimedOutStatus = "TimedOut";

    private readonly IRunnerProvider _runnerProvider;
    private readonly IRunStore _runStore;
    private readonly OutputMarkerParser _outputMarkerParser;
    private readonly StepEnvironmentFileReader _environmentFileReader;
    private readonly ActionResolver _actionResolver;
    private readonly IDependencyCache _dependencyCache;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly Func<int, TimeSpan> _createTimeout;

    public JobExecutor(
        IRunnerProvider runnerProvider,
        IRunStore runStore,
        OutputMarkerParser outputMarkerParser,
        ActionResolver actionResolver,
        IDependencyCache dependencyCache,
        Func<int, TimeSpan>? createJobTimeout = null)
    {
        _runnerProvider = runnerProvider;
        _runStore = runStore;
        _outputMarkerParser = outputMarkerParser;
        _environmentFileReader = new StepEnvironmentFileReader();
        _actionResolver = actionResolver;
        _dependencyCache = dependencyCache;
        _conditionEvaluator = new ConditionEvaluator();
        _createTimeout = createJobTimeout ?? (minutes => TimeSpan.FromMinutes(minutes));
    }

    public async Task<JobExecutionOutcome> ExecuteAsync(
        string workflowName,
        WorkflowJob job,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults workflowDefaults,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> workflowVariables,
        IReadOnlyDictionary<string, string> workflowSecrets,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
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
        var environmentUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathEntries = new List<string>();
        var secretMasker = new SecretMasker();
        var previousStepStatuses = new List<string>();
        var stepStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var hardFailureSeen = false;
        var artifacts = new List<WorkflowRunArtifact>();
        var pendingDependencyCacheSaves = new List<DependencyCacheSaveRequest>();
        var runDefaults = workflowDefaults.Merge(job.Defaults);
        using var timeoutTokenSource = CreateTimeoutTokenSource(job, cancellationToken);
        var jobCancellationToken = timeoutTokenSource?.Token ?? cancellationToken;
        var currentStepIndex = -1;
        WorkflowStep? currentStep = null;
        DateTimeOffset? currentStepStartedAt = null;
        JobServiceNetwork? serviceNetwork = null;
        var servicesStopped = false;

        foreach (var secret in workflowSecrets.Values)
        {
            secretMasker.Add(secret);
        }

        if (!_runnerProvider.SupportsRunner(job.RunsOn))
        {
            errors.Add($"workflow.jobs.{job.Name}.runs-on '{job.RunsOn}' is not supported by the configured runner provider.");
            stepRecords.AddRange(CreateSkippedStepRecords(job.Steps));
            return CompleteJob(job, FailedStatus, startedAt, outputs, stepRecords, artifacts, errors, 0, 0, job.Steps.Count, 0);
        }

        try
        {
            var serviceStart = await StartServiceContainersAsync(job, projectRoot, output, jobCancellationToken);
            if (!serviceStart.Success)
            {
                errors.AddRange(serviceStart.Errors);
                stepRecords.AddRange(CreateSkippedStepRecords(job.Steps));
                return CompleteJob(job, FailedStatus, startedAt, outputs, stepRecords, artifacts, errors, 0, 0, job.Steps.Count, 0);
            }

            serviceNetwork = serviceStart.Network;

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
                    AddStepStatus(stepStatuses, step, skippedRecord.Status);
                    continue;
                }

                var condition = _conditionEvaluator.EvaluateStep(
                    step.If,
                    ExecutionExpressionContexts.ForStep(
                        workflowName,
                        job,
                        step,
                        projectRoot,
                        runId,
                        runTrigger,
                        CreateStepContextEnvironment(workflowEnv, job.Env, environmentUpdates, step.Env),
                        workflowVariables,
                        workflowSecrets,
                        jobOutputs,
                        jobStatuses,
                        stepOutputs,
                        stepStatuses),
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
                    AddStepStatus(stepStatuses, step, failedRecord.Status);
                    continue;
                }

                if (!condition.ShouldRun)
                {
                    output.WriteLine($"[{job.DisplayName}] {step.Name} (skipped)");
                    var skippedRecord = CreateSkippedStepRecord(step);
                    stepRecords.Add(skippedRecord);
                    skippedSteps++;
                    previousStepStatuses.Add(skippedRecord.Status);
                    AddStepStatus(stepStatuses, step, skippedRecord.Status);
                    continue;
                }

                output.WriteLine($"[{job.DisplayName}] {step.Name}");

                var stepStartedAt = DateTimeOffset.UtcNow;
                currentStepStartedAt = stepStartedAt;
                var stepResult = await ExecuteStepAsync(
                    workflowName,
                    job,
                    step,
                    index,
                    workflowEnv,
                    runDefaults,
                    jobOutputs,
                    jobStatuses,
                    stepOutputs,
                    environmentUpdates,
                    pathEntries,
                    secretMasker,
                    workflowVariables,
                    workflowSecrets,
                    availableArtifacts,
                    artifacts,
                    projectRoot,
                    runId,
                    runTrigger,
                    stepStatuses,
                    serviceNetwork,
                    output,
                    error,
                    jobCancellationToken);
                var stepFinishedAt = DateTimeOffset.UtcNow;

                outputs.Merge(stepResult.Outputs);
                environmentUpdates.Merge(stepResult.EnvironmentUpdates);
                pathEntries.AddRange(stepResult.PathEntries);
                if (step.Id is not null && stepResult.Outputs.Count > 0)
                {
                    stepOutputs[step.Id] = new Dictionary<string, string>(stepResult.Outputs, StringComparer.Ordinal);
                }

                if (stepResult.PendingDependencyCacheSave is not null)
                {
                    pendingDependencyCacheSaves.Add(stepResult.PendingDependencyCacheSave);
                }

                artifacts.AddRange(stepResult.Artifacts);
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
                    secretMasker.Mask(stepResult.Command),
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
                    step.ContinueOnError,
                    stepResult.SummaryPath,
                    stepResult.Summary,
                    stepResult.Annotations));
                previousStepStatuses.Add(stepResult.Status);
                AddStepStatus(stepStatuses, step, stepResult.Status);

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
                await SaveDependencyCachesAsync(
                    pendingDependencyCacheSaves,
                    job.DisplayName,
                    output,
                    errors,
                    jobCancellationToken);
            }

            if (errors.Count == 0)
            {
                errors.AddRange(FindDuplicateArtifactNameErrors(
                    availableArtifacts.Concat(artifacts),
                    job.Artifacts.Select(artifact => artifact.Name),
                    $"workflow.jobs.{job.Name}.artifacts"));
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

            await StopServiceContainersAsync(serviceNetwork, errors, CancellationToken.None);
            servicesStopped = true;

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
        finally
        {
            if (!servicesStopped)
            {
                await StopServiceContainersAsync(serviceNetwork, errors, CancellationToken.None);
            }
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

    private async Task<ServiceContainerStartResult> StartServiceContainersAsync(
        WorkflowJob job,
        string projectRoot,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (job.Services.Count == 0)
        {
            return ServiceContainerStartResult.Started(null);
        }

        output.WriteLine($"[{job.DisplayName}] Starting service containers");
        return await _runnerProvider.StartServiceContainersAsync(
            new ServiceContainerStartRequest(
                job.Name,
                projectRoot,
                CreateServiceDefinitions(job, projectRoot)),
            cancellationToken);
    }

    private async Task StopServiceContainersAsync(
        JobServiceNetwork? serviceNetwork,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (serviceNetwork is null)
        {
            return;
        }

        var stopResult = await _runnerProvider.StopServiceContainersAsync(serviceNetwork, cancellationToken);
        errors.AddRange(stopResult.Errors);
    }

    private async Task<StepExecutionOutcome> ExecuteStepAsync(
        string workflowName,
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        IReadOnlyDictionary<string, string> workflowEnv,
        WorkflowRunDefaults runDefaults,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyList<string> pathEntries,
        SecretMasker secretMasker,
        IReadOnlyDictionary<string, string> workflowVariables,
        IReadOnlyDictionary<string, string> workflowSecrets,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
        IReadOnlyList<WorkflowRunArtifact> jobArtifacts,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> stepStatuses,
        JobServiceNetwork? serviceNetwork,
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

        await using var collector = new StepOutputCollector(output, error, stepLog, _outputMarkerParser, secretMasker);
        var effectiveRunDefaults = runDefaults.Merge(new WorkflowRunDefaults(step.Shell, step.WorkingDirectory));
        StepExecutionPlan? plan = null;
        StepEnvironmentFiles environmentFiles;

        try
        {
            environmentFiles = await _runStore.CreateStepEnvironmentFilesAsync(runId, job.Name, stepIndex, step.Name, stepCancellationToken);
        }
        catch (OperationCanceledException) when (IsStepTimeout(timeoutTokenSource, cancellationToken))
        {
            return StepExecutionOutcome.TimedOut(
                step.Run ?? step.Uses ?? string.Empty,
                collector.LogPath,
                new Dictionary<string, string>(),
                null,
                null,
                FormatStepTimeoutError(job.Name, step.Name, step.TimeoutMinutes!.Value));
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(
                StorageError.Format($"creating workflow environment files for job '{job.Name}' step '{step.Name}'", ex),
                collector.LogPath);
        }

        try
        {
            plan = await ResolveStepExecutionAsync(
                workflowName,
                job,
                step,
                stepIndex,
                workflowEnv,
                jobOutputs,
                jobStatuses,
                stepOutputs,
                environmentUpdates,
                workflowVariables,
                workflowSecrets,
                projectRoot,
                runId,
                runTrigger,
                stepStatuses,
                stepCancellationToken);
            if (!plan.Success)
            {
                return StepExecutionOutcome.FailedWithoutExitCode(step.Uses ?? string.Empty, plan.Errors, collector.LogPath);
            }

            if (plan.Kind == StepExecutionKind.DependencyCacheAction)
            {
                return await ExecuteDependencyCacheActionAsync(
                    plan.DependencyCacheAction!,
                    projectRoot,
                    collector,
                    stepCancellationToken);
            }

            if (plan.Kind == StepExecutionKind.ArtifactAction)
            {
                return await ExecuteArtifactActionAsync(
                    plan.ArtifactAction!,
                    job.Name,
                    projectRoot,
                    runId,
                    availableArtifacts,
                    jobArtifacts,
                    collector,
                    stepCancellationToken);
            }

            var environment = CreateStepEnvironment(
                job.Container?.Env ?? new Dictionary<string, string>(),
                workflowEnv,
                job.Env,
                stepOutputs,
                environmentUpdates,
                pathEntries,
                step.Env,
                workflowVariables,
                DefaultEnvironmentVariables.Create(workflowName, job, step, stepIndex, runId, runTrigger),
                CreateEnvironmentFileVariables(),
                plan.Environment);
            var oidcFailure = CreateOidcEnvironmentFailure(environment, GetStepCommand(plan, step), collector.LogPath);
            if (oidcFailure is not null)
            {
                return oidcFailure;
            }

            if (plan.Kind == StepExecutionKind.CompositeAction)
            {
                return await ExecuteCompositeActionAsync(
                    job,
                    step,
                    stepIndex,
                    plan,
                    environment,
                    effectiveRunDefaults,
                    projectRoot,
                    runId,
                    serviceNetwork,
                    collector,
                    stepCancellationToken);
            }

            var additionalMounts = plan.AdditionalMounts
                .Concat([new StepExecutionMount(environmentFiles.DirectoryPath, StepEnvironmentFileContainerDirectory, ReadOnly: false)])
                .ToArray();
            var result = plan.Kind switch
            {
                StepExecutionKind.DockerfileAction => await _runnerProvider.ExecuteDockerfileActionAsync(
                    new DockerfileActionExecutionRequest(
                        job.Name,
                        step.Name,
                        plan.DockerImage!,
                        projectRoot,
                        plan.DockerfileBuildContext!,
                        plan.DockerfilePath!,
                        environment,
                        additionalMounts,
                        serviceNetwork,
                        plan.DockerEntryPoint,
                        plan.DockerArguments),
                    collector,
                    stepCancellationToken),
                StepExecutionKind.DockerImageAction => await _runnerProvider.ExecuteDockerActionAsync(
                    new DockerActionExecutionRequest(
                        job.Name,
                        step.Name,
                        plan.DockerImage!,
                        projectRoot,
                        environment,
                        additionalMounts,
                        serviceNetwork,
                        plan.DockerEntryPoint,
                        plan.DockerArguments),
                    collector,
                    stepCancellationToken),
                StepExecutionKind.JavaScriptAction => await _runnerProvider.ExecuteJavaScriptActionAsync(
                    new JavaScriptActionExecutionRequest(
                        job.Name,
                        step.Name,
                        projectRoot,
                        plan.JavaScriptActionPath!,
                        plan.JavaScriptMain!,
                        environment,
                        additionalMounts,
                        serviceNetwork,
                        plan.JavaScriptPre,
                        plan.JavaScriptPost),
                    collector,
                    stepCancellationToken),
                _ => await _runnerProvider.ExecuteStepAsync(
                    new StepExecutionRequest(
                        job.Name,
                        step.Name,
                        job.RunsOn,
                        plan.Command!,
                        projectRoot,
                        environment,
                        effectiveRunDefaults.Shell,
                        effectiveRunDefaults.WorkingDirectory,
                        additionalMounts,
                        CreateContainerExecutionOptions(job.Container, projectRoot),
                        serviceNetwork),
                    collector,
                    stepCancellationToken)
            };
            var resultShell = plan.Kind == StepExecutionKind.ShellCommand ? effectiveRunDefaults.Shell : null;
            var resultWorkingDirectory = plan.Kind == StepExecutionKind.ShellCommand ? effectiveRunDefaults.WorkingDirectory : null;
            var environmentFileResult = await _environmentFileReader.ReadAsync(environmentFiles, stepCancellationToken);
            var outputs = MergeOutputs(collector.CapturedOutputs, MaskValues(environmentFileResult.Outputs, collector));
            var summary = environmentFileResult.Summary is null ? null : collector.Mask(environmentFileResult.Summary);

            if (environmentFileResult.Errors.Count > 0)
            {
                return StepExecutionOutcome.FailedWithoutExitCode(
                    plan.Command!,
                    environmentFileResult.Errors,
                    collector.LogPath,
                    outputs,
                    environmentFileResult.Environment,
                    environmentFileResult.PathEntries,
                    environmentFileResult.SummaryPath,
                    summary,
                    collector.Annotations);
            }

            if (!result.Success)
            {
                return StepExecutionOutcome.Failed(
                    plan.Command!,
                    result.ExitCode,
                    collector.LogPath,
                    outputs,
                    resultShell,
                    resultWorkingDirectory,
                    $"workflow.jobs.{job.Name}.steps.{step.Name} failed with exit code {result.ExitCode}.",
                    environmentFileResult.Environment,
                    environmentFileResult.PathEntries,
                    environmentFileResult.SummaryPath,
                    summary,
                    collector.Annotations);
            }

            return StepExecutionOutcome.Succeeded(
                plan.Command!,
                result.ExitCode,
                collector.LogPath,
                outputs,
                resultShell,
                resultWorkingDirectory,
                environmentFileResult.Environment,
                environmentFileResult.PathEntries,
                environmentFileResult.SummaryPath,
                summary,
                collector.Annotations);
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
                FormatStepTimeoutError(job.Name, step.Name, step.TimeoutMinutes!.Value),
                collector.Annotations);
        }
    }

    private async Task<StepExecutionOutcome> ExecuteCompositeActionAsync(
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        StepExecutionPlan plan,
        IReadOnlyDictionary<string, string> baseEnvironment,
        WorkflowRunDefaults effectiveRunDefaults,
        string projectRoot,
        string runId,
        JobServiceNetwork? serviceNetwork,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        var environmentUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathEntries = new List<string>();
        var actionStepOutputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var actionStepStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var summaryParts = new List<string>();
        string? summaryPath = null;

        foreach (var actionStep in plan.CompositeSteps)
        {
            var actionStepResult = actionStep.IsNestedAction
                ? await ExecuteNestedActionStepAsync(
                    job,
                    step,
                    actionStep,
                    stepIndex,
                    baseEnvironment,
                    actionStepOutputs,
                    environmentUpdates,
                    pathEntries,
                    effectiveRunDefaults,
                    projectRoot,
                    runId,
                    serviceNetwork,
                    collector,
                    cancellationToken)
                : await ExecuteCompositeRunStepAsync(
                    job,
                    step,
                    actionStep,
                    stepIndex,
                    plan,
                    baseEnvironment,
                    actionStepOutputs,
                    environmentUpdates,
                    pathEntries,
                    effectiveRunDefaults,
                    projectRoot,
                    runId,
                    serviceNetwork,
                    collector,
                    cancellationToken);

            if (actionStep.Id is not null)
            {
                actionStepOutputs[actionStep.Id] = new Dictionary<string, string>(actionStepResult.Outputs, StringComparer.Ordinal);
                actionStepStatuses[actionStep.Id] = actionStepResult.Status;
            }

            environmentUpdates.Merge(actionStepResult.EnvironmentUpdates);
            pathEntries.AddRange(actionStepResult.PathEntries);

            if (actionStepResult.Summary is not null)
            {
                summaryPath = actionStepResult.SummaryPath;
                summaryParts.Add(actionStepResult.Summary);
            }

            if (IsFailureStatus(actionStepResult.Status))
            {
                return actionStepResult with
                {
                    Command = plan.Command!,
                    Outputs = collector.CapturedOutputs,
                    EnvironmentUpdates = environmentUpdates,
                    PathEntries = pathEntries,
                    SummaryPath = summaryPath,
                    Summary = JoinSummaries(summaryParts)
                };
            }
        }

        var outputResolution = ResolveCompositeActionOutputs(
            plan.CompositeInputs,
            plan.CompositeOutputExpressions,
            actionStepOutputs,
            actionStepStatuses,
            projectRoot,
            collector);
        var outputs = MergeOutputs(collector.CapturedOutputs, outputResolution.Outputs);

        if (outputResolution.Errors.Count > 0)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                plan.Command!,
                outputResolution.Errors,
                collector.LogPath,
                outputs,
                environmentUpdates,
                pathEntries,
                summaryPath,
                JoinSummaries(summaryParts),
                collector.Annotations);
        }

        return StepExecutionOutcome.Succeeded(
            plan.Command!,
            0,
            collector.LogPath,
            outputs,
            null,
            null,
            environmentUpdates,
            pathEntries,
            summaryPath,
            JoinSummaries(summaryParts),
            collector.Annotations);
    }

    private async Task<StepExecutionOutcome> ExecuteDependencyCacheActionAsync(
        DependencyCacheAction action,
        string projectRoot,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        DependencyCacheRestoreResult restore;
        try
        {
            restore = await _dependencyCache.RestoreAsync(
                new DependencyCacheRestoreRequest(projectRoot, action.Key, action.RestoreKeys, action.Paths),
                cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(
                StorageError.Format($"restoring dependency cache '{action.Key}'", ex),
                collector.LogPath);
        }

        if (!restore.Success)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                "actions/cache",
                restore.Errors,
                collector.LogPath,
                CreateDependencyCacheOutputs(action.Key, restore));
        }

        if (restore.MatchedKey is null)
        {
            await collector.WriteOutputLineAsync($"Dependency cache not found for key '{action.Key}'.", cancellationToken);
        }
        else if (restore.CacheHit)
        {
            await collector.WriteOutputLineAsync($"Dependency cache restored from key '{restore.MatchedKey}'.", cancellationToken);
        }
        else
        {
            await collector.WriteOutputLineAsync($"Dependency cache restored from key '{restore.MatchedKey}'.", cancellationToken);
        }

        var pendingSave = restore.CacheHit
            ? null
            : new DependencyCacheSaveRequest(projectRoot, action.Key, action.Paths);

        return StepExecutionOutcome.Succeeded(
            "actions/cache",
            0,
            collector.LogPath,
            CreateDependencyCacheOutputs(action.Key, restore),
            null,
            null,
            annotations: collector.Annotations,
            pendingDependencyCacheSave: pendingSave);
    }

    private async Task<StepExecutionOutcome> ExecuteArtifactActionAsync(
        ArtifactAction action,
        string jobName,
        string projectRoot,
        string runId,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
        IReadOnlyList<WorkflowRunArtifact> jobArtifacts,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        return action.Kind == ArtifactActionKind.Upload
            ? await ExecuteUploadArtifactActionAsync(
                action,
                jobName,
                projectRoot,
                runId,
                availableArtifacts,
                jobArtifacts,
                collector,
                cancellationToken)
            : await ExecuteDownloadArtifactActionAsync(
                action,
                projectRoot,
                availableArtifacts,
                jobArtifacts,
                collector,
                cancellationToken);
    }

    private async Task<StepExecutionOutcome> ExecuteUploadArtifactActionAsync(
        ArtifactAction action,
        string jobName,
        string projectRoot,
        string runId,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
        IReadOnlyList<WorkflowRunArtifact> jobArtifacts,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        var artifactName = action.Name!;
        var duplicateErrors = FindDuplicateArtifactNameErrors(
            availableArtifacts.Concat(jobArtifacts),
            [artifactName],
            "actions/upload-artifact");
        if (duplicateErrors.Count > 0)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                "actions/upload-artifact",
                duplicateErrors,
                collector.LogPath,
                annotations: collector.Annotations);
        }

        ArtifactSaveResult save;
        try
        {
            save = await _runStore.SaveArtifactAsync(
                runId,
                jobName,
                projectRoot,
                artifactName,
                action.Paths,
                action.RetentionDays,
                cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(
                StorageError.Format($"saving artifact '{artifactName}'", ex),
                collector.LogPath);
        }

        if (save.Errors.Count > 0)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                "actions/upload-artifact",
                save.Errors,
                collector.LogPath,
                annotations: collector.Annotations);
        }

        await collector.WriteOutputLineAsync($"Artifact '{artifactName}' uploaded.", cancellationToken);

        return StepExecutionOutcome.Succeeded(
            "actions/upload-artifact",
            0,
            collector.LogPath,
            collector.CapturedOutputs,
            null,
            null,
            annotations: collector.Annotations,
            artifacts: save.Artifacts);
    }

    private async Task<StepExecutionOutcome> ExecuteDownloadArtifactActionAsync(
        ArtifactAction action,
        string projectRoot,
        IReadOnlyList<WorkflowRunArtifact> availableArtifacts,
        IReadOnlyList<WorkflowRunArtifact> jobArtifacts,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        var artifacts = availableArtifacts.Concat(jobArtifacts).ToArray();
        var selectedArtifacts = action.Name is null
            ? artifacts
            : artifacts.Where(artifact => string.Equals(artifact.Name, action.Name, StringComparison.Ordinal)).ToArray();

        if (selectedArtifacts.Length == 0)
        {
            var message = action.Name is null
                ? "actions/download-artifact found no artifacts in this run."
                : $"actions/download-artifact artifact '{action.Name}' was not found in this run.";
            return StepExecutionOutcome.FailedWithoutExitCode(
                "actions/download-artifact",
                [message],
                collector.LogPath,
                annotations: collector.Annotations);
        }

        ArtifactDownloadResult download;
        try
        {
            download = await _runStore.RestoreArtifactsAsync(
                projectRoot,
                selectedArtifacts,
                action.DestinationPath,
                useArtifactNameSubdirectories: action.Name is null,
                cancellationToken);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepExecutionOutcome.StorageFailed(
                StorageError.Format("restoring artifacts", ex),
                collector.LogPath);
        }

        if (download.Errors.Count > 0)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                "actions/download-artifact",
                download.Errors,
                collector.LogPath,
                annotations: collector.Annotations);
        }

        foreach (var restoredPath in download.RestoredPaths)
        {
            await collector.WriteOutputLineAsync($"Artifact restored: {restoredPath}", cancellationToken);
        }

        return StepExecutionOutcome.Succeeded(
            "actions/download-artifact",
            0,
            collector.LogPath,
            collector.CapturedOutputs,
            null,
            null,
            annotations: collector.Annotations);
    }

    private async Task SaveDependencyCachesAsync(
        IReadOnlyList<DependencyCacheSaveRequest> pendingSaves,
        string jobDisplayName,
        TextWriter output,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        foreach (var pendingSave in pendingSaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine($"[{jobDisplayName}] Save cache {pendingSave.Key}");

            DependencyCacheSaveResult save;
            try
            {
                save = await _dependencyCache.SaveAsync(pendingSave, cancellationToken);
            }
            catch (Exception ex) when (StorageError.IsRecoverable(ex))
            {
                errors.Add(StorageError.Format($"saving dependency cache '{pendingSave.Key}'", ex));
                continue;
            }

            foreach (var message in save.Messages)
            {
                output.WriteLine(message);
            }

            if (!save.Success)
            {
                errors.AddRange(save.Errors);
                continue;
            }

            if (save.Saved)
            {
                output.WriteLine($"Saved dependency cache '{pendingSave.Key}'.");
            }
        }
    }

    private async Task<StepExecutionOutcome> ExecuteCompositeRunStepAsync(
        WorkflowJob job,
        WorkflowStep step,
        CompositeActionStepPlan actionStep,
        int stepIndex,
        StepExecutionPlan plan,
        IReadOnlyDictionary<string, string> baseEnvironment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> actionStepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyList<string> pathEntries,
        WorkflowRunDefaults effectiveRunDefaults,
        string projectRoot,
        string runId,
        JobServiceNetwork? serviceNetwork,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        var environmentFilesResult = await CreateCompositeActionStepEnvironmentFilesAsync(
            runId,
            job.Name,
            stepIndex,
            step.Name,
            actionStep.Name,
            cancellationToken);
        if (!environmentFilesResult.Success)
        {
            return StepExecutionOutcome.StorageFailed(environmentFilesResult.Error!, collector.LogPath);
        }

        var environmentFiles = environmentFilesResult.Files!;
        var environment = CreateCompositeActionStepEnvironment(
            baseEnvironment,
            actionStepOutputs,
            environmentUpdates,
            pathEntries,
            CreateEnvironmentFileVariables());
        var oidcFailure = CreateOidcEnvironmentFailure(environment, actionStep.Command!, collector.LogPath);
        if (oidcFailure is not null)
        {
            return oidcFailure;
        }

        var additionalMounts = plan.AdditionalMounts
            .Concat([new StepExecutionMount(environmentFiles.DirectoryPath, StepEnvironmentFileContainerDirectory, ReadOnly: false)])
            .ToArray();
        var request = new StepExecutionRequest(
            job.Name,
            $"{step.Name} / {actionStep.Name}",
            job.RunsOn,
            actionStep.Command!,
            projectRoot,
            environment,
            actionStep.Shell ?? effectiveRunDefaults.Shell,
            actionStep.WorkingDirectory ?? effectiveRunDefaults.WorkingDirectory,
            additionalMounts,
            CreateContainerExecutionOptions(job.Container, projectRoot),
            serviceNetwork);
        var result = await _runnerProvider.ExecuteStepAsync(request, collector, cancellationToken);
        var environmentFileResult = await _environmentFileReader.ReadAsync(environmentFiles, cancellationToken);
        var outputs = MaskValues(environmentFileResult.Outputs, collector);
        var summary = environmentFileResult.Summary is null ? null : collector.Mask(environmentFileResult.Summary);

        if (environmentFileResult.Errors.Count > 0)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                actionStep.Command!,
                environmentFileResult.Errors,
                collector.LogPath,
                outputs,
                environmentFileResult.Environment,
                environmentFileResult.PathEntries,
                environmentFileResult.SummaryPath,
                summary,
                collector.Annotations);
        }

        if (!result.Success)
        {
            return StepExecutionOutcome.Failed(
                actionStep.Command!,
                result.ExitCode,
                collector.LogPath,
                outputs,
                actionStep.Shell ?? effectiveRunDefaults.Shell,
                actionStep.WorkingDirectory ?? effectiveRunDefaults.WorkingDirectory,
                $"workflow.jobs.{job.Name}.steps.{step.Name} action step '{actionStep.Name}' failed with exit code {result.ExitCode}.",
                environmentFileResult.Environment,
                environmentFileResult.PathEntries,
                environmentFileResult.SummaryPath,
                summary,
                collector.Annotations);
        }

        return StepExecutionOutcome.Succeeded(
            actionStep.Command!,
            result.ExitCode,
            collector.LogPath,
            outputs,
            actionStep.Shell ?? effectiveRunDefaults.Shell,
            actionStep.WorkingDirectory ?? effectiveRunDefaults.WorkingDirectory,
            environmentFileResult.Environment,
            environmentFileResult.PathEntries,
            environmentFileResult.SummaryPath,
            summary,
            collector.Annotations);
    }

    private async Task<StepExecutionOutcome> ExecuteNestedActionStepAsync(
        WorkflowJob job,
        WorkflowStep step,
        CompositeActionStepPlan actionStep,
        int stepIndex,
        IReadOnlyDictionary<string, string> baseEnvironment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> actionStepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyList<string> pathEntries,
        WorkflowRunDefaults effectiveRunDefaults,
        string projectRoot,
        string runId,
        JobServiceNetwork? serviceNetwork,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        foreach (var warning in actionStep.NestedAction!.Warnings)
        {
            await collector.WriteErrorLineAsync($"warning: {warning}", cancellationToken);
        }

        var nestedPlan = CreateStepExecutionPlan(actionStep.NestedAction);
        var nestedEnvironment = CreateCompositeActionStepEnvironment(
            baseEnvironment,
            actionStepOutputs,
            environmentUpdates,
            pathEntries,
            nestedPlan.Environment);
        var nestedStep = new WorkflowStep(
            $"{step.Name} / {actionStep.Name}",
            null,
            actionStep.Uses,
            Id: actionStep.Id,
            With: actionStep.With);

        if (nestedPlan.Kind == StepExecutionKind.CompositeAction)
        {
            return await ExecuteCompositeActionAsync(
                job,
                nestedStep,
                stepIndex,
                nestedPlan,
                nestedEnvironment,
                effectiveRunDefaults,
                projectRoot,
                runId,
                serviceNetwork,
                collector,
                cancellationToken);
        }

        return await ExecuteNestedNonCompositeActionAsync(
            job,
            nestedStep,
            actionStep.Name,
            stepIndex,
            nestedPlan,
            nestedEnvironment,
            effectiveRunDefaults,
            projectRoot,
            runId,
            serviceNetwork,
            collector,
            cancellationToken);
    }

    private async Task<StepExecutionOutcome> ExecuteNestedNonCompositeActionAsync(
        WorkflowJob job,
        WorkflowStep step,
        string actionStepName,
        int stepIndex,
        StepExecutionPlan nestedPlan,
        IReadOnlyDictionary<string, string> environment,
        WorkflowRunDefaults effectiveRunDefaults,
        string projectRoot,
        string runId,
        JobServiceNetwork? serviceNetwork,
        StepOutputCollector collector,
        CancellationToken cancellationToken)
    {
        var environmentFilesResult = await CreateCompositeActionStepEnvironmentFilesAsync(
            runId,
            job.Name,
            stepIndex,
            step.Name,
            actionStepName,
            cancellationToken);
        if (!environmentFilesResult.Success)
        {
            return StepExecutionOutcome.StorageFailed(environmentFilesResult.Error!, collector.LogPath);
        }

        var environmentFiles = environmentFilesResult.Files!;
        var actionEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        actionEnvironment.Merge(CreateEnvironmentFileVariables());
        var oidcFailure = CreateOidcEnvironmentFailure(actionEnvironment, GetStepCommand(nestedPlan, step), collector.LogPath);
        if (oidcFailure is not null)
        {
            return oidcFailure;
        }

        var additionalMounts = nestedPlan.AdditionalMounts
            .Concat([new StepExecutionMount(environmentFiles.DirectoryPath, StepEnvironmentFileContainerDirectory, ReadOnly: false)])
            .ToArray();
        var result = nestedPlan.Kind switch
        {
            StepExecutionKind.DockerfileAction => await _runnerProvider.ExecuteDockerfileActionAsync(
                new DockerfileActionExecutionRequest(
                    job.Name,
                    step.Name,
                    nestedPlan.DockerImage!,
                    projectRoot,
                    nestedPlan.DockerfileBuildContext!,
                    nestedPlan.DockerfilePath!,
                    actionEnvironment,
                    additionalMounts,
                    serviceNetwork,
                    nestedPlan.DockerEntryPoint,
                    nestedPlan.DockerArguments),
                collector,
                cancellationToken),
            StepExecutionKind.DockerImageAction => await _runnerProvider.ExecuteDockerActionAsync(
                new DockerActionExecutionRequest(
                    job.Name,
                    step.Name,
                    nestedPlan.DockerImage!,
                    projectRoot,
                    actionEnvironment,
                    additionalMounts,
                    serviceNetwork,
                    nestedPlan.DockerEntryPoint,
                    nestedPlan.DockerArguments),
                collector,
                cancellationToken),
            StepExecutionKind.JavaScriptAction => await _runnerProvider.ExecuteJavaScriptActionAsync(
                new JavaScriptActionExecutionRequest(
                    job.Name,
                    step.Name,
                    projectRoot,
                    nestedPlan.JavaScriptActionPath!,
                    nestedPlan.JavaScriptMain!,
                    actionEnvironment,
                    additionalMounts,
                    serviceNetwork,
                    nestedPlan.JavaScriptPre,
                    nestedPlan.JavaScriptPost),
                collector,
                cancellationToken),
            _ => await _runnerProvider.ExecuteStepAsync(
                new StepExecutionRequest(
                    job.Name,
                    step.Name,
                    job.RunsOn,
                    nestedPlan.Command!,
                    projectRoot,
                    actionEnvironment,
                    effectiveRunDefaults.Shell,
                    effectiveRunDefaults.WorkingDirectory,
                    additionalMounts,
                    CreateContainerExecutionOptions(job.Container, projectRoot),
                    serviceNetwork),
                collector,
                cancellationToken)
        };
        var environmentFileResult = await _environmentFileReader.ReadAsync(environmentFiles, cancellationToken);
        var outputs = MaskValues(environmentFileResult.Outputs, collector);
        var summary = environmentFileResult.Summary is null ? null : collector.Mask(environmentFileResult.Summary);

        if (environmentFileResult.Errors.Count > 0)
        {
            return StepExecutionOutcome.FailedWithoutExitCode(
                nestedPlan.Command!,
                environmentFileResult.Errors,
                collector.LogPath,
                outputs,
                environmentFileResult.Environment,
                environmentFileResult.PathEntries,
                environmentFileResult.SummaryPath,
                summary,
                collector.Annotations);
        }

        if (!result.Success)
        {
            return StepExecutionOutcome.Failed(
                nestedPlan.Command!,
                result.ExitCode,
                collector.LogPath,
                outputs,
                nestedPlan.Kind == StepExecutionKind.ShellCommand ? effectiveRunDefaults.Shell : null,
                nestedPlan.Kind == StepExecutionKind.ShellCommand ? effectiveRunDefaults.WorkingDirectory : null,
                $"workflow.jobs.{job.Name}.steps.{step.Name} failed with exit code {result.ExitCode}.",
                environmentFileResult.Environment,
                environmentFileResult.PathEntries,
                environmentFileResult.SummaryPath,
                summary,
                collector.Annotations);
        }

        return StepExecutionOutcome.Succeeded(
            nestedPlan.Command!,
            result.ExitCode,
            collector.LogPath,
            outputs,
            nestedPlan.Kind == StepExecutionKind.ShellCommand ? effectiveRunDefaults.Shell : null,
            nestedPlan.Kind == StepExecutionKind.ShellCommand ? effectiveRunDefaults.WorkingDirectory : null,
            environmentFileResult.Environment,
            environmentFileResult.PathEntries,
            environmentFileResult.SummaryPath,
            summary,
            collector.Annotations);
    }

    private async Task<StepEnvironmentFileCreationResult> CreateCompositeActionStepEnvironmentFilesAsync(
        string runId,
        string jobName,
        int stepIndex,
        string workflowStepName,
        string actionStepName,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = await _runStore.CreateStepEnvironmentFilesAsync(
                runId,
                jobName,
                stepIndex,
                $"{workflowStepName} {actionStepName}",
                cancellationToken);
            return StepEnvironmentFileCreationResult.Created(files);
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return StepEnvironmentFileCreationResult.Failed(
                StorageError.Format($"creating workflow environment files for job '{jobName}' action step '{actionStepName}'", ex));
        }
    }

    private static IReadOnlyDictionary<string, string> CreateCompositeActionStepEnvironment(
        IReadOnlyDictionary<string, string> baseEnvironment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> actionStepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyList<string> pathEntries,
        IReadOnlyDictionary<string, string> environmentFileEnv)
    {
        var environment = new Dictionary<string, string>(baseEnvironment, StringComparer.Ordinal);
        environment.Merge(environmentUpdates);
        environment.Merge(CreateStepOutputEnvironment(actionStepOutputs));
        environment.ApplyPathEntries(pathEntries);
        environment.Merge(environmentFileEnv);
        return environment;
    }

    private static CompositeActionOutputResolution ResolveCompositeActionOutputs(
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string> outputExpressions,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> actionStepOutputs,
        IReadOnlyDictionary<string, string> actionStepStatuses,
        string projectRoot,
        StepOutputCollector collector)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var expressionContext = ExecutionExpressionContexts.ForActionOutputs(
            inputs,
            actionStepOutputs,
            actionStepStatuses,
            projectRoot);
        var evaluationContext = new ExpressionEvaluationContext(
            expressionContext.Resolve,
            workspaceRoot: projectRoot);

        foreach (var output in outputExpressions)
        {
            var interpolation = ExpressionTemplate.Interpolate(output.Value, evaluationContext);
            if (interpolation.Success)
            {
                outputs[output.Key] = collector.Mask(interpolation.Value);
                continue;
            }

            foreach (var error in interpolation.Errors)
            {
                errors.Add($"action.outputs.{output.Key}.value could not be evaluated: {error}");
            }
        }

        return new CompositeActionOutputResolution(outputs, errors);
    }

    private static string? JoinSummaries(IReadOnlyList<string> summaryParts)
        => summaryParts.Count == 0 ? null : string.Concat(summaryParts);

    private async Task<StepExecutionPlan> ResolveStepExecutionAsync(
        string workflowName,
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyDictionary<string, string> workflowVariables,
        IReadOnlyDictionary<string, string> workflowSecrets,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> stepStatuses,
        CancellationToken cancellationToken)
    {
        if (step.Run is not null)
        {
            return StepExecutionPlan.ShellCommand(step.Run);
        }

        var resolvedWith = ResolveStepWith(
            workflowName,
            job,
            step,
            stepIndex,
            workflowEnv,
            jobOutputs,
            jobStatuses,
            stepOutputs,
            environmentUpdates,
            workflowVariables,
            workflowSecrets,
            projectRoot,
            runId,
            runTrigger,
            stepStatuses);
        if (!resolvedWith.Success)
        {
            return StepExecutionPlan.Failed(resolvedWith.Errors);
        }

        var dependencyCacheAction = ResolveDependencyCacheAction(step.Uses, resolvedWith.Values);
        if (dependencyCacheAction.Success && dependencyCacheAction.Action is not null)
        {
            return StepExecutionPlan.DependencyCache(dependencyCacheAction.Action);
        }

        if (!dependencyCacheAction.Success)
        {
            return StepExecutionPlan.Failed(dependencyCacheAction.Errors);
        }

        var artifactAction = ResolveArtifactAction(step.Uses, resolvedWith.Values);
        if (artifactAction.Success && artifactAction.Action is not null)
        {
            return StepExecutionPlan.Artifact(artifactAction.Action);
        }

        if (!artifactAction.Success)
        {
            return StepExecutionPlan.Failed(artifactAction.Errors);
        }

        var setupAction = SetupActionResolver.Resolve(step.Uses, resolvedWith.Values);
        if (setupAction.Success && setupAction.Action is not null)
        {
            return StepExecutionPlan.ShellCommand(SetupActionCommandBuilder.Build(setupAction.Action));
        }

        if (!setupAction.Success)
        {
            return StepExecutionPlan.Failed(setupAction.Errors);
        }

        var resolvedStep = step with { With = resolvedWith.Values };
        var action = await _actionResolver.ResolveAsync(resolvedStep, projectRoot, cancellationToken);
        if (!action.Success)
        {
            return StepExecutionPlan.Failed(action.Errors);
        }

        return CreateStepExecutionPlan(action);
    }

    private static DependencyCacheActionResolution ResolveDependencyCacheAction(
        string? uses,
        IReadOnlyDictionary<string, string> with)
    {
        if (uses is null ||
            !ActionReference.TryParse(uses, out var reference) ||
            !reference!.TryGetGitHubAction(out var githubAction) ||
            !IsDependencyCacheAction(githubAction!))
        {
            return DependencyCacheActionResolution.NotDependencyCacheAction;
        }

        var errors = new List<string>();
        foreach (var key in with.Keys)
        {
            if (key is not "path" and not "key" and not "restore-keys")
            {
                errors.Add($"actions/cache with.{key} is not supported yet.");
            }
        }

        var cacheKey = ReadRequiredCacheInput(with, "key", errors);
        var paths = SplitCacheInput(ReadRequiredCacheInput(with, "path", errors));
        var restoreKeys = SplitCacheInput(ReadOptionalCacheInput(with, "restore-keys"));

        if (paths.Count == 0)
        {
            errors.Add("actions/cache with.path is required.");
        }

        return errors.Count == 0
            ? DependencyCacheActionResolution.Resolved(new DependencyCacheAction(cacheKey!, restoreKeys, paths))
            : DependencyCacheActionResolution.Failed(errors);
    }

    private static bool IsDependencyCacheAction(GitHubActionReference action)
    {
        return string.Equals(action.Owner, "actions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Repository, "cache", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(action.ActionPath);
    }

    private static ArtifactActionResolution ResolveArtifactAction(
        string? uses,
        IReadOnlyDictionary<string, string> with)
    {
        if (uses is null ||
            !ActionReference.TryParse(uses, out var reference) ||
            !reference!.TryGetGitHubAction(out var githubAction) ||
            !IsArtifactAction(githubAction!))
        {
            return ArtifactActionResolution.NotArtifactAction;
        }

        return string.Equals(githubAction!.Repository, "upload-artifact", StringComparison.OrdinalIgnoreCase)
            ? ResolveUploadArtifactAction(with)
            : ResolveDownloadArtifactAction(with);
    }

    private static ArtifactActionResolution ResolveUploadArtifactAction(IReadOnlyDictionary<string, string> with)
    {
        var errors = new List<string>();
        foreach (var key in with.Keys)
        {
            if (key is not "name" and not "path" and not "retention-days")
            {
                errors.Add($"actions/upload-artifact with.{key} is not supported yet.");
            }
        }

        var artifactName = ReadOptionalActionInput(with, "name") ?? "artifact";
        var paths = SplitActionInput(ReadRequiredActionInput(with, "path", errors));
        var retentionDays = ReadRetentionDays(with, errors);

        if (paths.Count == 0)
        {
            errors.Add("actions/upload-artifact with.path is required.");
        }

        return errors.Count == 0
            ? ArtifactActionResolution.Resolved(ArtifactAction.Upload(artifactName, paths, retentionDays))
            : ArtifactActionResolution.Failed(errors);
    }

    private static ArtifactActionResolution ResolveDownloadArtifactAction(IReadOnlyDictionary<string, string> with)
    {
        var errors = new List<string>();
        foreach (var key in with.Keys)
        {
            if (key is not "name" and not "path")
            {
                errors.Add($"actions/download-artifact with.{key} is not supported yet.");
            }
        }

        var artifactName = ReadOptionalActionInput(with, "name");
        var destinationPath = ReadOptionalActionInput(with, "path") ?? ".";

        return errors.Count == 0
            ? ArtifactActionResolution.Resolved(ArtifactAction.Download(artifactName, destinationPath))
            : ArtifactActionResolution.Failed(errors);
    }

    private static bool IsArtifactAction(GitHubActionReference action)
    {
        return string.Equals(action.Owner, "actions", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(action.ActionPath) &&
            (string.Equals(action.Repository, "upload-artifact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action.Repository, "download-artifact", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadRequiredCacheInput(
        IReadOnlyDictionary<string, string> with,
        string name,
        List<string> errors)
    {
        var value = ReadOptionalCacheInput(with, name);
        if (value is null)
        {
            errors.Add($"actions/cache with.{name} is required.");
        }

        return value;
    }

    private static string? ReadOptionalCacheInput(
        IReadOnlyDictionary<string, string> with,
        string name)
    {
        return with.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static IReadOnlyList<string> SplitCacheInput(string? value)
    {
        if (value is null)
        {
            return [];
        }

        return value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string? ReadRequiredActionInput(
        IReadOnlyDictionary<string, string> with,
        string name,
        List<string> errors)
    {
        var value = ReadOptionalActionInput(with, name);
        if (value is null)
        {
            errors.Add($"actions/upload-artifact with.{name} is required.");
        }

        return value;
    }

    private static string? ReadOptionalActionInput(
        IReadOnlyDictionary<string, string> with,
        string name)
    {
        return with.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static IReadOnlyList<string> SplitActionInput(string? value)
    {
        if (value is null)
        {
            return [];
        }

        return value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static int? ReadRetentionDays(
        IReadOnlyDictionary<string, string> with,
        List<string> errors)
    {
        var value = ReadOptionalActionInput(with, "retention-days");
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, out var retentionDays) || retentionDays <= 0)
        {
            errors.Add("actions/upload-artifact with.retention-days must be a positive integer.");
            return null;
        }

        return retentionDays;
    }

    private static IReadOnlyDictionary<string, string> CreateDependencyCacheOutputs(
        string primaryKey,
        DependencyCacheRestoreResult restore)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache-hit"] = restore.CacheHit ? "true" : "false",
            ["cache-primary-key"] = primaryKey,
            ["cache-matched-key"] = restore.MatchedKey ?? string.Empty
        };
    }

    private static IReadOnlyList<string> FindDuplicateArtifactNameErrors(
        IEnumerable<WorkflowRunArtifact> existingArtifacts,
        IEnumerable<string> newArtifactNames,
        string context)
    {
        var existingNames = existingArtifacts
            .Select(artifact => artifact.Name)
            .ToHashSet(StringComparer.Ordinal);
        var newNames = new HashSet<string>(StringComparer.Ordinal);
        var reportedNames = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var artifactName in newArtifactNames)
        {
            if ((!newNames.Add(artifactName) || existingNames.Contains(artifactName)) &&
                reportedNames.Add(artifactName))
            {
                errors.Add($"{context} artifact '{artifactName}' already exists in this run.");
            }
        }

        return errors;
    }

    private static StepWithResolution ResolveStepWith(
        string workflowName,
        WorkflowJob job,
        WorkflowStep step,
        int stepIndex,
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jobOutputs,
        IReadOnlyDictionary<string, string> jobStatuses,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyDictionary<string, string> workflowVariables,
        IReadOnlyDictionary<string, string> workflowSecrets,
        string projectRoot,
        string runId,
        WorkflowRunTrigger runTrigger,
        IReadOnlyDictionary<string, string> stepStatuses)
    {
        if (step.With.Count == 0)
        {
            return StepWithResolution.Resolved(step.With);
        }

        var expressionContext = ExecutionExpressionContexts.ForStep(
            workflowName,
            job,
            step,
            projectRoot,
            runId,
            runTrigger,
            CreateStepContextEnvironment(workflowEnv, job.Env, environmentUpdates, step.Env),
            workflowVariables,
            workflowSecrets,
            jobOutputs,
            jobStatuses,
            stepOutputs,
            stepStatuses);
        var evaluationContext = new ExpressionEvaluationContext(
            expressionContext.Resolve,
            workspaceRoot: projectRoot);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var item in step.With)
        {
            var interpolation = ExpressionTemplate.Interpolate(item.Value, evaluationContext);
            if (interpolation.Success)
            {
                values[item.Key] = interpolation.Value;
                continue;
            }

            foreach (var error in interpolation.Errors)
            {
                errors.Add($"workflow.jobs.{job.Name}.steps[{stepIndex}].with.{item.Key} could not be evaluated: {error}");
            }
        }

        return errors.Count == 0
            ? StepWithResolution.Resolved(values)
            : StepWithResolution.Failed(errors);
    }

    private static StepExecutionPlan CreateStepExecutionPlan(ActionResolutionResult action)
    {
        if (action.IsDockerfileAction)
        {
            return StepExecutionPlan.DockerfileAction(
                action.Command!,
                action.DockerImage!,
                action.DockerfileBuildContext!,
                action.DockerfilePath!,
                action.Environment,
                action.AdditionalMounts);
        }

        if (action.IsDockerImageAction)
        {
            return StepExecutionPlan.DockerImageAction(
                action.Command!,
                action.DockerImage!,
                action.Environment,
                action.DockerEntryPoint,
                action.DockerArguments);
        }

        if (action.IsJavaScriptAction)
        {
            return StepExecutionPlan.JavaScriptAction(
                action.Command!,
                action.JavaScriptActionPath!,
                action.JavaScriptMain!,
                action.JavaScriptPre,
                action.JavaScriptPost,
                action.Environment,
                action.AdditionalMounts);
        }

        if (action.IsCompositeAction)
        {
            return StepExecutionPlan.CompositeAction(
                action.Command!,
                action.CompositeSteps,
                action.CompositeInputs,
                action.CompositeOutputExpressions,
                action.Environment,
                action.AdditionalMounts);
        }

        return StepExecutionPlan.ShellCommand(action.Command!, action.Environment, action.AdditionalMounts);
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
            job.Concurrency?.CancelInProgress ?? false,
            job.Matrix,
            job.Environment);

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
        IReadOnlyDictionary<string, string> containerEnv,
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, string> jobEnv,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stepOutputs,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyList<string> pathEntries,
        IReadOnlyDictionary<string, string> stepEnv,
        IReadOnlyDictionary<string, string> workflowVariables,
        IReadOnlyDictionary<string, string> defaultEnv,
        IReadOnlyDictionary<string, string> environmentFileEnv,
        IReadOnlyDictionary<string, string> actionEnv)
    {
        var environment = new Dictionary<string, string>(containerEnv, StringComparer.Ordinal);
        environment.Merge(workflowEnv);
        environment.Merge(jobEnv);
        environment.Merge(environmentUpdates);
        environment.Merge(CreateStepOutputEnvironment(stepOutputs));
        environment.Merge(stepEnv);
        environment.Merge(CreateLocalValueEnvironment("ACTIO_VAR_", workflowVariables));
        environment.ApplyPathEntries(pathEntries);
        environment.MergeDefaultEnvironment(defaultEnv);
        environment.Merge(environmentFileEnv);
        environment.Merge(actionEnv);
        return environment;
    }

    private static string GetStepCommand(StepExecutionPlan plan, WorkflowStep step)
        => plan.Command ?? step.Run ?? step.Uses ?? string.Empty;

    private static StepExecutionOutcome? CreateOidcEnvironmentFailure(
        IReadOnlyDictionary<string, string> environment,
        string command,
        string? logPath)
    {
        var errors = OidcTokenPolicy.ValidateEnvironment(environment);
        return errors.Count == 0
            ? null
            : StepExecutionOutcome.FailedWithoutExitCode(command, errors, logPath);
    }

    private static IReadOnlyDictionary<string, string> CreateLocalValueEnvironment(
        string prefix,
        IReadOnlyDictionary<string, string> values)
    {
        return values.ToDictionary(
            item => $"{prefix}{ToEnvironmentSegment(item.Key)}",
            item => item.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<StepExecutionMount> CreateContainerVolumeMounts(
        WorkflowJobContainer? container,
        string projectRoot)
    {
        if (container is null || container.Volumes.Count == 0)
        {
            return [];
        }

        return container.Volumes
            .Select(volume => new StepExecutionMount(
                Path.Combine(projectRoot, volume.Source),
                volume.Target,
                volume.ReadOnly))
            .ToArray();
    }

    private static JobContainerExecutionOptions? CreateContainerExecutionOptions(
        WorkflowJobContainer? container,
        string projectRoot)
    {
        return container is null
            ? null
            : new JobContainerExecutionOptions(
                container.Image,
                container.Ports,
                container.Options,
                CreateContainerVolumeMounts(container, projectRoot));
    }

    private static IReadOnlyList<ServiceContainerDefinition> CreateServiceDefinitions(
        WorkflowJob job,
        string projectRoot)
    {
        return job.Services
            .Select(service => new ServiceContainerDefinition(
                service.Key,
                service.Value.Image,
                service.Value.Env,
                service.Value.Ports,
                service.Value.Options,
                CreateServiceVolumeMounts(service.Value, projectRoot)))
            .ToArray();
    }

    private static IReadOnlyList<StepExecutionMount> CreateServiceVolumeMounts(
        WorkflowJobService service,
        string projectRoot)
    {
        if (service.Volumes.Count == 0)
        {
            return [];
        }

        return service.Volumes
            .Select(volume => new StepExecutionMount(
                Path.Combine(projectRoot, volume.Source),
                volume.Target,
                volume.ReadOnly))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> CreateStepContextEnvironment(
        IReadOnlyDictionary<string, string> workflowEnv,
        IReadOnlyDictionary<string, string> jobEnv,
        IReadOnlyDictionary<string, string> environmentUpdates,
        IReadOnlyDictionary<string, string> stepEnv)
    {
        var environment = new Dictionary<string, string>(workflowEnv, StringComparer.Ordinal);
        environment.Merge(jobEnv);
        environment.Merge(environmentUpdates);
        environment.Merge(stepEnv);
        return environment;
    }

    private static void AddStepStatus(
        Dictionary<string, string> stepStatuses,
        WorkflowStep step,
        string status)
    {
        if (step.Id is not null)
        {
            stepStatuses[step.Id] = status;
        }
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

    private static IReadOnlyDictionary<string, string> CreateEnvironmentFileVariables()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_ENV"] = ToEnvironmentFileContainerPath(StepEnvironmentFiles.EnvironmentFileName),
            ["GITHUB_OUTPUT"] = ToEnvironmentFileContainerPath(StepEnvironmentFiles.OutputFileName),
            ["GITHUB_PATH"] = ToEnvironmentFileContainerPath(StepEnvironmentFiles.PathFileName),
            ["GITHUB_STEP_SUMMARY"] = ToEnvironmentFileContainerPath(StepEnvironmentFiles.StepSummaryFileName),
            ["GITHUB_STATE"] = ToEnvironmentFileContainerPath(StepEnvironmentFiles.StateFileName)
        };
    }

    private static string ToEnvironmentFileContainerPath(string fileName)
    {
        return $"{StepEnvironmentFileContainerDirectory}/{fileName}";
    }

    private static IReadOnlyDictionary<string, string> MergeOutputs(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var outputs = new Dictionary<string, string>(first, StringComparer.Ordinal);
        outputs.Merge(second);
        return outputs;
    }

    private static IReadOnlyDictionary<string, string> MaskValues(
        IReadOnlyDictionary<string, string> values,
        StepOutputCollector collector)
    {
        return values.ToDictionary(
            item => item.Key,
            item => collector.Mask(item.Value),
            StringComparer.Ordinal);
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
        IReadOnlyDictionary<string, string> EnvironmentUpdates,
        IReadOnlyList<string> PathEntries,
        string? SummaryPath,
        string? Summary,
        IReadOnlyList<StepLogAnnotation> Annotations,
        bool CountsAsFailedStep,
        IReadOnlyList<WorkflowRunArtifact> Artifacts,
        DependencyCacheSaveRequest? PendingDependencyCacheSave)
    {
        public static StepExecutionOutcome Succeeded(
            string command,
            int exitCode,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string? shell,
            string? workingDirectory,
            IReadOnlyDictionary<string, string>? environmentUpdates = null,
            IReadOnlyList<string>? pathEntries = null,
            string? summaryPath = null,
            string? summary = null,
            IReadOnlyList<StepLogAnnotation>? annotations = null,
            IReadOnlyList<WorkflowRunArtifact>? artifacts = null,
            DependencyCacheSaveRequest? pendingDependencyCacheSave = null)
        {
            return new StepExecutionOutcome(SuccessStatus, command, exitCode, logPath, shell, workingDirectory, outputs, [], environmentUpdates ?? new Dictionary<string, string>(), pathEntries ?? [], summaryPath, summary, annotations ?? [], false, artifacts ?? [], pendingDependencyCacheSave);
        }

        public static StepExecutionOutcome Failed(
            string command,
            int exitCode,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string? shell,
            string? workingDirectory,
            string error,
            IReadOnlyDictionary<string, string>? environmentUpdates = null,
            IReadOnlyList<string>? pathEntries = null,
            string? summaryPath = null,
            string? summary = null,
            IReadOnlyList<StepLogAnnotation>? annotations = null)
        {
            return new StepExecutionOutcome(FailedStatus, command, exitCode, logPath, shell, workingDirectory, outputs, [error], environmentUpdates ?? new Dictionary<string, string>(), pathEntries ?? [], summaryPath, summary, annotations ?? [], true, [], null);
        }

        public static StepExecutionOutcome StorageFailed(string error, string? logPath = null)
        {
            return new StepExecutionOutcome(FailedStatus, string.Empty, null, logPath, null, null, new Dictionary<string, string>(), [error], new Dictionary<string, string>(), [], null, null, [], false, [], null);
        }

        public static StepExecutionOutcome TimedOut(
            string command,
            string? logPath,
            IReadOnlyDictionary<string, string> outputs,
            string? shell,
            string? workingDirectory,
            string error,
            IReadOnlyList<StepLogAnnotation>? annotations = null)
        {
            return new StepExecutionOutcome(TimedOutStatus, command, null, logPath, shell, workingDirectory, outputs, [error], new Dictionary<string, string>(), [], null, null, annotations ?? [], true, [], null);
        }

        public static StepExecutionOutcome FailedWithoutExitCode(
            string command,
            IReadOnlyList<string> errors,
            string? logPath,
            IReadOnlyDictionary<string, string>? outputs = null,
            IReadOnlyDictionary<string, string>? environmentUpdates = null,
            IReadOnlyList<string>? pathEntries = null,
            string? summaryPath = null,
            string? summary = null,
            IReadOnlyList<StepLogAnnotation>? annotations = null)
        {
            return new StepExecutionOutcome(FailedStatus, command, null, logPath, null, null, outputs ?? new Dictionary<string, string>(), errors, environmentUpdates ?? new Dictionary<string, string>(), pathEntries ?? [], summaryPath, summary, annotations ?? [], true, [], null);
        }
    }

    private sealed record CompositeActionOutputResolution(
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> Errors);

    private sealed record StepWithResolution(
        bool Success,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyList<string> Errors)
    {
        public static StepWithResolution Resolved(IReadOnlyDictionary<string, string> values)
            => new(true, values, []);

        public static StepWithResolution Failed(IReadOnlyList<string> errors)
            => new(false, new Dictionary<string, string>(), errors);
    }

    private sealed record StepEnvironmentFileCreationResult(
        bool Success,
        StepEnvironmentFiles? Files,
        string? Error)
    {
        public static StepEnvironmentFileCreationResult Created(StepEnvironmentFiles files)
            => new(true, files, null);

        public static StepEnvironmentFileCreationResult Failed(string error)
            => new(false, null, error);
    }

    private enum StepExecutionKind
    {
        ShellCommand,
        CompositeAction,
        DockerImageAction,
        DockerfileAction,
        JavaScriptAction,
        DependencyCacheAction,
        ArtifactAction
    }

    private sealed record StepExecutionPlan(
        bool Success,
        StepExecutionKind Kind,
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
        DependencyCacheAction? DependencyCacheAction,
        ArtifactAction? ArtifactAction,
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
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                environment ?? new Dictionary<string, string>(),
                additionalMounts ?? [],
                []);
        }

        public static StepExecutionPlan CompositeAction(
            string command,
            IReadOnlyList<CompositeActionStepPlan> steps,
            IReadOnlyDictionary<string, string> inputs,
            IReadOnlyDictionary<string, string> outputExpressions,
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyList<StepExecutionMount> additionalMounts)
        {
            return new(
                true,
                StepExecutionKind.CompositeAction,
                command,
                steps,
                inputs,
                outputExpressions,
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                environment,
                additionalMounts,
                []);
        }

        public static StepExecutionPlan DockerImageAction(
            string command,
            string dockerImage,
            IReadOnlyDictionary<string, string> environment,
            string? dockerEntryPoint,
            IReadOnlyList<string> dockerArguments)
        {
            return new(
                true,
                StepExecutionKind.DockerImageAction,
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
                null,
                null,
                environment,
                [],
                []);
        }

        public static StepExecutionPlan DockerfileAction(
            string command,
            string dockerImage,
            string buildContext,
            string dockerfilePath,
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyList<StepExecutionMount> additionalMounts)
        {
            return new(
                true,
                StepExecutionKind.DockerfileAction,
                command,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                dockerImage,
                null,
                [],
                buildContext,
                dockerfilePath,
                null,
                null,
                null,
                null,
                null,
                null,
                environment,
                additionalMounts,
                []);
        }

        public static StepExecutionPlan JavaScriptAction(
            string command,
            string actionPath,
            string main,
            string? pre,
            string? post,
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyList<StepExecutionMount> additionalMounts)
        {
            return new(
                true,
                StepExecutionKind.JavaScriptAction,
                command,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null,
                null,
                [],
                null,
                null,
                actionPath,
                main,
                pre,
                post,
                null,
                null,
                environment,
                additionalMounts,
                []);
        }

        public static StepExecutionPlan DependencyCache(DependencyCacheAction action)
        {
            return new(
                true,
                StepExecutionKind.DependencyCacheAction,
                "actions/cache",
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                action,
                null,
                new Dictionary<string, string>(),
                [],
                []);
        }

        public static StepExecutionPlan Artifact(ArtifactAction action)
        {
            var command = action.Kind == ArtifactActionKind.Upload
                ? "actions/upload-artifact"
                : "actions/download-artifact";
            return new(
                true,
                StepExecutionKind.ArtifactAction,
                command,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                action,
                new Dictionary<string, string>(),
                [],
                []);
        }

        public static StepExecutionPlan Failed(IReadOnlyList<string> errors)
            => new(
                false,
                StepExecutionKind.ShellCommand,
                null,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new Dictionary<string, string>(),
                [],
                errors);
    }

    private sealed record DependencyCacheActionResolution(
        bool Success,
        DependencyCacheAction? Action,
        IReadOnlyList<string> Errors)
    {
        public static DependencyCacheActionResolution NotDependencyCacheAction { get; } = new(true, null, []);

        public static DependencyCacheActionResolution Resolved(DependencyCacheAction action)
            => new(true, action, []);

        public static DependencyCacheActionResolution Failed(IReadOnlyList<string> errors)
            => new(false, null, errors);
    }

    private sealed record ArtifactActionResolution(
        bool Success,
        ArtifactAction? Action,
        IReadOnlyList<string> Errors)
    {
        public static ArtifactActionResolution NotArtifactAction { get; } = new(true, null, []);

        public static ArtifactActionResolution Resolved(ArtifactAction action)
            => new(true, action, []);

        public static ArtifactActionResolution Failed(IReadOnlyList<string> errors)
            => new(false, null, errors);
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

    public static void MergeDefaultEnvironment(this Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var item in source)
        {
            if (string.Equals(item.Key, "CI", StringComparison.Ordinal) && target.ContainsKey(item.Key))
            {
                continue;
            }

            target[item.Key] = item.Value;
        }
    }

    public static void ApplyPathEntries(this Dictionary<string, string> target, IReadOnlyList<string> pathEntries)
    {
        if (pathEntries.Count == 0)
        {
            return;
        }

        var basePath = target.TryGetValue("PATH", out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : JobExecutor.DefaultContainerPath;
        target["PATH"] = string.Join(":", pathEntries.Concat([basePath]));
    }
}
