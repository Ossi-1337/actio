using Actio.Core.Actions;

namespace Actio.Engine.Execution;

internal static class SensitiveValueGuard
{
    public static IReadOnlyList<string> ValidateBuiltInPersistenceInputs(
        string? uses,
        IReadOnlyDictionary<string, string> inputs,
        SecretMasker secretMasker)
    {
        if (!IsPersistenceAction(uses))
        {
            return [];
        }

        return inputs
            .Where(input => secretMasker.ContainsSensitiveValue(input.Value))
            .Select(input =>
                $"{uses} with.{input.Key} cannot contain a registered secret.")
            .ToArray();
    }

    private static bool IsPersistenceAction(string? uses)
    {
        if (uses is null ||
            !ActionReference.TryParse(uses, out var reference) ||
            !reference!.TryGetGitHubAction(out var action) ||
            !string.Equals(action!.Owner, "actions", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(action.ActionPath))
        {
            return false;
        }

        return action.Repository.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
            action.Repository.Equals("upload-artifact", StringComparison.OrdinalIgnoreCase) ||
            action.Repository.Equals("download-artifact", StringComparison.OrdinalIgnoreCase);
    }
}
