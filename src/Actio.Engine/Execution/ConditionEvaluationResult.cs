namespace Actio.Engine.Execution;

internal sealed record ConditionEvaluationResult(
    bool Success,
    bool ShouldRun,
    string? Error)
{
    public static ConditionEvaluationResult Run()
    {
        return new ConditionEvaluationResult(true, true, null);
    }

    public static ConditionEvaluationResult Skip()
    {
        return new ConditionEvaluationResult(true, false, null);
    }

    public static ConditionEvaluationResult Failed(string error)
    {
        return new ConditionEvaluationResult(false, false, error);
    }
}
