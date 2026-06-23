namespace Actio.Storage;

public static class ActioHome
{
    public static string Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable("ACTIO_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            try
            {
                return Path.GetFullPath(overridePath);
            }
            catch (ArgumentException)
            {
                return overridePath;
            }
            catch (NotSupportedException)
            {
                return overridePath;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Actio");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Actio");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "actio");
    }
}
