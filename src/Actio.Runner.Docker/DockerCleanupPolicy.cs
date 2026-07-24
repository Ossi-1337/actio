using System.ComponentModel;
using System.Diagnostics;

namespace Actio.Runner.Docker;

internal static class DockerCleanupPolicy
{
    internal static DockerCleanupDecision Evaluate(
        IReadOnlyDictionary<string, string> labels,
        string instanceId)
    {
        if (!labels.TryGetValue("io.actio.instance", out var resourceInstance) ||
            !string.Equals(resourceInstance, instanceId, StringComparison.Ordinal))
        {
            return DockerCleanupDecision.SkipForeign;
        }

        if (!labels.TryGetValue("io.actio.owner-pid", out var processIdText) ||
            !int.TryParse(processIdText, out var processId) ||
            !labels.TryGetValue("io.actio.owner-start", out var processStartText) ||
            !long.TryParse(processStartText, out var processStart))
        {
            return DockerCleanupDecision.SkipUnverifiable;
        }

        return GetOwnerState(processId, processStart) switch
        {
            DockerResourceOwnerState.Active => DockerCleanupDecision.SkipActive,
            DockerResourceOwnerState.Stale => DockerCleanupDecision.Remove,
            _ => DockerCleanupDecision.SkipUnverifiable
        };
    }

    internal static DockerResourceOwnerState GetOwnerState(
        int processId,
        long expectedStart)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == expectedStart
                ? DockerResourceOwnerState.Active
                : DockerResourceOwnerState.Stale;
        }
        catch (ArgumentException)
        {
            return DockerResourceOwnerState.Stale;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return DockerResourceOwnerState.Unknown;
        }
    }
}

internal enum DockerCleanupDecision
{
    Remove,
    SkipActive,
    SkipForeign,
    SkipUnverifiable
}

internal enum DockerResourceOwnerState
{
    Active,
    Stale,
    Unknown
}
