using Actio.Core.Workflows;
using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

public sealed class WorkflowExecutor : IWorkflowExecutor
{
    private const string SuccessStatus = "Success";
    private const string FailedStatus = "Failed";
    private const string SkippedStatus = "Skipped";

    private readonly IRunnerProvider _runnerProvider;
    private readonly IRunStore _runStore;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly OutputMarkerParser _outputMarkerParser;

    public WorkflowExecutor(IRunnerProvider runnerProvider, IRunStore? runStore = null)
    {
        _runnerProvider = runnerProvider;
        _runStore = runStore ?? new NullRunStore();
        _conditionEvaluator = new ConditionEvaluator();
        _outputMarkerParser = new OutputMarkerParser();
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDocument workflow,
        WorkflowExecutionOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var runId = options.RunId ?? _runStore.CreateRunId();
        var storagePaths = await _runStore.InitializeRunAsync(runId, cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var totalSteps = workflow.StepCount;
        var successfulSteps = 0;
        var errors = new List<string>();
        var jobRecords = new List<JobRunRecord>();
        var runOutputs = new List<WorkflowRunOutput>();
        var runArtifacts = new List<WorkflowRunArtifact>();
        var jobStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var jobOutputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var plan = JobGraphPlanner.Plan(workflow.Jobs);

        if (plan.Errors.Count > 0)
        {
            errors.AddRange(plan.Errors);
        }
        else
        {
            foreach (var job in plan.Jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var skipReason = GetDependencySkipReason(job, jobStatuses);
                if (skipReason is null)
                {
                    var condition = _conditionEvaluator.Evaluate(job.If, jobOutputs);

                    if (!condition.Success)
                    {
                        skipReason = condition.Error;
                        errors.Add($"workflow.jobs.{job.Name}.if could not be evaluated: {condition.Error}");
                    }
                    else if (!condition.ShouldRun)
                    {
                        skipReason = "if condition evaluated to false.";
                    }
                }

                JobExecutionOutcome outcome;
                if (skipReason is not null)
                {
                    outcome = new JobExecutionOutcome(CreateSkippedJobRecord(job, skipReason), 0);
                }
                else
                {
                    outcome = await ExecuteJobAsync(
                        job,
                        workflow.Env,
                        options.ProjectRoot,
                        runId,
                        output,
                        error,
                        cancellationToken);
                }

                successfulSteps += outcome.SuccessfulSteps;
                jobRecords.Add(outcome.Job);
                jobStatuses[job.Name] = outcome.Job.Status;
                jobOutputs[job.Name] = outcome.Job.Outputs;
                errors.AddRange(outcome.Job.Status == FailedStatus ? outcome.Job.Errors : []);
                runArtifacts.AddRange(outcome.Job.Artifacts);
                runOutputs.AddRange(outcome.Job.Outputs.Select(item =>
                    new WorkflowRunOutput(job.Name, item.Key, item.Value)));
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var status = errors.Count == 0 ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Failed;
        var runRecord = new WorkflowRunRecord(
            runId,
            workflow.Name,
            options.WorkflowPath,
            options.ProjectRoot,
            status.ToString(),
            startedAt,
            finishedAt,
            ToDurationMilliseconds(startedAt, finishedAt),
            jobRecords,
            runOutputs,
            runArtifacts,
            errors);

        await _runStore.SaveRunRecordAsync(runRecord, cancellationToken);

        return new WorkflowExecutionResult(
            status,
            successfulSteps,
            totalSteps,
            errors,
            runOutputs,
            runArtifacts,
            runId,
            storagePaths.RunRecordPath);
    }

    private async Task<JobExecutionOutcome> ExecuteJobAsync(
        WorkflowJob job,
        IReadOnlyDictionary<string, string> workflowEnv,
        string projectRoot,
        string runId,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var successfulSteps = 0;
        var errors = new List<string>();
        var stepRecords = new List<StepRunRecord>();
        var outputs = new Dictionary<string, string>(job.Outputs, StringComparer.Ordinal);
        var artifacts = new List<WorkflowRunArtifact>();

        if (!_runnerProvider.SupportsRunner(job.RunsOn))
        {
            errors.Add($"workflow.jobs.{job.Name}.runs-on '{job.RunsOn}' is not supported by the configured runner provider.");
            stepRecords.AddRange(CreateSkippedStepRecords(job.Steps));
            return CompleteJob(job, FailedStatus, startedAt, outputs, stepRecords, artifacts, errors, successfulSteps);
        }

        for (var index = 0; index < job.Steps.Count; index++)
        {
            var step = job.Steps[index];
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine($"[{job.Name}] {step.Name}");

            var stepStartedAt = DateTimeOffset.UtcNow;
            var result = await _runnerProvider.ExecuteStepAsync(
                new StepExecutionRequest(
                    job.Name,
                    step.Name,
                    job.RunsOn,
                    step.Run!,
                    projectRoot,
                    CreateStepEnvironment(workflowEnv)),
                output,
                error,
                cancellationToken);
            var stepFinishedAt = DateTimeOffset.UtcNow;
            var logPath = await _runStore.WriteStepLogAsync(
                runId,
                job.Name,
                index,
                step.Name,
                result.OutputLines,
                result.ErrorLines,
                cancellationToken);

            foreach (var capturedOutput in _outputMarkerParser.Parse(result.OutputLines))
            {
                outputs[capturedOutput.Key] = capturedOutput.Value;
            }

            stepRecords.Add(new StepRunRecord(
                step.Name,
                result.Success ? SuccessStatus : FailedStatus,
                step.Run!,
                result.ExitCode,
                logPath,
                stepStartedAt,
                stepFinishedAt,
                ToDurationMilliseconds(stepStartedAt, stepFinishedAt)));

            if (!result.Success)
            {
                errors.Add($"workflow.jobs.{job.Name}.steps.{step.Name} failed with exit code {result.ExitCode}.");
                stepRecords.AddRange(CreateSkippedStepRecords(job.Steps.Skip(index + 1)));
                break;
            }

            successfulSteps++;
        }

        if (errors.Count == 0)
        {
            var artifactResult = await _runStore.SaveArtifactsAsync(
                runId,
                job.Name,
                projectRoot,
                job.Artifacts,
                cancellationToken);

            artifacts.AddRange(artifactResult.Artifacts);
            errors.AddRange(artifactResult.Errors);
        }

        return CompleteJob(
            job,
            errors.Count == 0 ? SuccessStatus : FailedStatus,
            startedAt,
            outputs,
            stepRecords,
            artifacts,
            errors,
            successfulSteps);
    }

    private static JobExecutionOutcome CompleteJob(
        WorkflowJob job,
        string status,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyList<StepRunRecord> stepRecords,
        IReadOnlyList<WorkflowRunArtifact> artifacts,
        IReadOnlyList<string> errors,
        int successfulSteps)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var record = new JobRunRecord(
            job.Name,
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
            errors);

        return new JobExecutionOutcome(record, successfulSteps);
    }

    private static JobRunRecord CreateSkippedJobRecord(WorkflowJob job, string reason)
    {
        return new JobRunRecord(
            job.Name,
            SkippedStatus,
            job.RunsOn,
            job.Needs,
            job.If,
            null,
            null,
            0,
            new Dictionary<string, string>(),
            CreateSkippedStepRecords(job.Steps),
            [],
            [reason]);
    }

    private static IReadOnlyList<StepRunRecord> CreateSkippedStepRecords(IEnumerable<WorkflowStep> steps)
    {
        return steps
            .Select(step => new StepRunRecord(step.Name, SkippedStatus, step.Run ?? string.Empty, null, null, null, null, 0))
            .ToArray();
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

    private static IReadOnlyDictionary<string, string> CreateStepEnvironment(
        IReadOnlyDictionary<string, string> workflowEnv)
    {
        return new Dictionary<string, string>(workflowEnv, StringComparer.Ordinal);
    }

    private static long ToDurationMilliseconds(DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        return Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds);
    }

    private sealed record JobExecutionOutcome(
        JobRunRecord Job,
        int SuccessfulSteps);
}
