namespace Actio.Engine.Execution;

internal static class OidcTokenPolicy
{
    public const string RequestUrlEnvironmentVariable = "ACTIONS_ID_TOKEN_REQUEST_URL";
    public const string RequestTokenEnvironmentVariable = "ACTIONS_ID_TOKEN_REQUEST_TOKEN";

    private static readonly string[] ReservedEnvironmentVariables =
    [
        RequestUrlEnvironmentVariable,
        RequestTokenEnvironmentVariable
    ];

    public static IReadOnlyList<string> ValidateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var errors = new List<string>();

        foreach (var variable in ReservedEnvironmentVariables)
        {
            if (!environment.ContainsKey(variable))
            {
                continue;
            }

            errors.Add(
                $"{variable} is reserved for GitHub OIDC token requests, but Actio does not issue OIDC tokens in local runs. Remove this variable or run the workflow in an environment with a trusted OIDC issuer.");
        }

        return errors;
    }
}
