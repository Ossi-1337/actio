namespace Actio.Core.Workflows;

public static class WorkflowShells
{
    public const string Bash = "bash";
    public const string PowerShell = "pwsh";
    public const string Sh = "sh";

    public const string SupportedValues = "bash, pwsh, or sh";

    public static bool IsSupported(string shell)
    {
        return shell is Bash or PowerShell or Sh;
    }
}
