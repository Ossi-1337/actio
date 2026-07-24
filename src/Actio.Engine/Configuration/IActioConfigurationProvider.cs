using Actio.Engine.Execution;

namespace Actio.Engine.Configuration;

public interface IActioConfigurationProvider
{
    ActioConfigurationLoadResult Load();
}

public sealed record ActioConfigurationLoadResult(
    bool Success,
    ContainerResourceConfiguration Configuration,
    ActioInstanceIdentity InstanceIdentity,
    IReadOnlyList<string>? Errors = null)
{
    public IReadOnlyList<string> Errors { get; init; } = Errors ?? [];
}
