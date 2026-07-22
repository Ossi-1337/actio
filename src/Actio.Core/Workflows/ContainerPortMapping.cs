namespace Actio.Core.Workflows;

public sealed record ContainerPortMapping(
    int ContainerPort,
    int? HostPort = null,
    string Protocol = "tcp")
{
    public const string TcpProtocol = "tcp";
    public const string UdpProtocol = "udp";

    public bool UsesDynamicHostPort => HostPort is null;
}
