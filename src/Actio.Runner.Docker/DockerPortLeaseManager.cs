using System.Collections.Concurrent;
using Actio.Core.Workflows;

namespace Actio.Runner.Docker;

internal sealed class DockerPortLeaseManager
{
    private readonly ConcurrentDictionary<PublishedPortKey, string> _leases = new();

    internal bool TryAcquire(
        string jobName,
        IReadOnlyList<ContainerPortMapping> ports,
        out string? error)
    {
        var keys = ports
            .Where(port => port.HostPort is not null)
            .Select(port => new PublishedPortKey(port.HostPort!.Value, port.Protocol))
            .ToArray();
        var duplicate = keys
            .GroupBy(key => key)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            error = $"secure-baseline blocked duplicate fixed loopback port {duplicate.Key.Port}/{duplicate.Key.Protocol} in job '{jobName}'. Use a different host port or omit it for dynamic assignment.";
            return false;
        }

        var acquired = new List<PublishedPortKey>();
        foreach (var key in keys)
        {
            if (_leases.TryAdd(key, jobName))
            {
                acquired.Add(key);
                continue;
            }

            foreach (var acquiredKey in acquired)
            {
                RemoveOwnedLease(acquiredKey, jobName);
            }

            _leases.TryGetValue(key, out var owner);
            error = $"secure-baseline could not reserve fixed loopback port {key.Port}/{key.Protocol} for job '{jobName}' because job '{owner ?? "unknown"}' already reserved it. Use a different host port or omit it for dynamic assignment.";
            return false;
        }

        error = null;
        return true;
    }

    internal void Release(string jobName, IEnumerable<ContainerPortMapping> ports)
    {
        foreach (var port in ports)
        {
            if (port.HostPort is int hostPort)
            {
                RemoveOwnedLease(new PublishedPortKey(hostPort, port.Protocol), jobName);
            }
        }
    }

    private void RemoveOwnedLease(PublishedPortKey key, string jobName)
        => ((ICollection<KeyValuePair<PublishedPortKey, string>>)_leases)
            .Remove(new KeyValuePair<PublishedPortKey, string>(key, jobName));

    private readonly record struct PublishedPortKey(int Port, string Protocol);
}
