namespace Actio.Runner.Docker;

public sealed class DockerImageResolver
{
    private readonly IReadOnlyDictionary<string, string> _images;

    public DockerImageResolver()
        : this(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ubuntu-latest"] = "ubuntu:24.04",
            ["ubuntu-24.04"] = "ubuntu:24.04",
            ["ubuntu-22.04"] = "ubuntu:22.04",
            ["alpine-latest"] = "alpine:3.20",
            ["alpine-3.20"] = "alpine:3.20"
        })
    {
    }

    public DockerImageResolver(IReadOnlyDictionary<string, string> images)
    {
        _images = images;
    }

    public bool TryResolveImage(string runsOn, out string image)
    {
        return _images.TryGetValue(runsOn, out image!);
    }
}
