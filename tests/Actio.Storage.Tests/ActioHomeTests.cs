using Actio.Storage;

namespace Actio.Storage.Tests;

public sealed class ActioHomeTests
{
    [Fact]
    public void Resolve_UsesActioHomeOverrideWhenSet()
    {
        var original = Environment.GetEnvironmentVariable("ACTIO_HOME");
        var overridePath = Path.Combine(Path.GetTempPath(), $"actio-home-{Guid.NewGuid():N}");

        try
        {
            Environment.SetEnvironmentVariable("ACTIO_HOME", overridePath);

            Assert.Equal(Path.GetFullPath(overridePath), ActioHome.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTIO_HOME", original);
        }
    }
}
