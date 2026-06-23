using System.Security;

namespace Actio.Engine.Execution;

internal static class StorageError
{
    public static bool IsRecoverable(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or ArgumentException;
    }

    public static string Format(string action, Exception exception)
    {
        return $"Storage error while {action}: {exception.Message}";
    }
}
