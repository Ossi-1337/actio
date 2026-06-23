using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed record JobExecutionOutcome(
    JobRunRecord Job,
    int SuccessfulSteps,
    int FailedSteps,
    int SkippedSteps);
