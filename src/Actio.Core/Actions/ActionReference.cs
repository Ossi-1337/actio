namespace Actio.Core.Actions;

public static class ActionReference
{
    public static bool IsSupportedLocalReference(string value)
    {
        return value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith(".\\", StringComparison.Ordinal);
    }
}
