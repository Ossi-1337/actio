namespace Actio.Engine.Runs;

public sealed record RunnerNetworkObservation(
    string JobName,
    string NetworkName,
    string Mode,
    bool OutboundAllowed,
    bool Internal,
    IReadOnlyList<string>? ServiceAliases = null,
    IReadOnlyList<RunnerPublishedPort>? PublishedPorts = null)
{
    public IReadOnlyList<string> ServiceAliases { get; init; } = ServiceAliases ?? [];

    public IReadOnlyList<RunnerPublishedPort> PublishedPorts { get; init; } = PublishedPorts ?? [];
}

public sealed record RunnerPublishedPort(
    string Surface,
    string BindAddress,
    int ContainerPort,
    int? HostPort,
    string Protocol)
{
    public string Assignment => HostPort is null ? "dynamic" : "fixed";
}
