namespace Actio.Engine.Runs;

public sealed record StepEnvironmentFiles(
    string DirectoryPath,
    string EnvironmentFilePath,
    string OutputFilePath,
    string PathFilePath,
    string StepSummaryFilePath,
    string StateFilePath)
{
    public const string EnvironmentFileName = "GITHUB_ENV";
    public const string OutputFileName = "GITHUB_OUTPUT";
    public const string PathFileName = "GITHUB_PATH";
    public const string StepSummaryFileName = "GITHUB_STEP_SUMMARY";
    public const string StateFileName = "GITHUB_STATE";
}
