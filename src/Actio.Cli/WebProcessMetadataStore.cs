using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Actio.Cli;

internal sealed class WebProcessMetadataStore
{
    private const int SchemaVersion = 1;
    private const long MaximumLogBytes = 1024 * 1024;
    private readonly string _actioHome;
    private readonly string _urlKey;

    public WebProcessMetadataStore(string actioHome, string url)
    {
        _actioHome = Path.GetFullPath(actioHome);
        _urlKey = CreateUrlKey(url);
    }

    public string MetadataPath => Path.Combine(_actioHome, "web", "processes", $"{_urlKey}.json");

    public string LaunchLockPath => Path.Combine(_actioHome, "web", "locks", $"launch-{_urlKey}.lock");

    public string LogPath => Path.Combine(_actioHome, "logs", "web", $"{_urlKey}.log");

    public static string GetRuntimeLockPath(string actioHome, string runtimeIdentity)
    {
        return Path.Combine(
            Path.GetFullPath(actioHome),
            "web",
            "locks",
            $"runtime-{runtimeIdentity}.lock");
    }

    public WebProcessMetadataReadResult Read()
    {
        if (!File.Exists(MetadataPath))
        {
            return WebProcessMetadataReadResult.Missing();
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<WebProcessMetadata>(File.ReadAllText(MetadataPath));
            return !IsValid(metadata)
                ? WebProcessMetadataReadResult.Corrupt("metadata schema is invalid")
                : WebProcessMetadataReadResult.Found(metadata!);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return WebProcessMetadataReadResult.Corrupt(ex.Message);
        }
    }

    public IReadOnlyList<WebProcessMetadataReadResult> ReadAll()
    {
        var directory = Path.GetDirectoryName(MetadataPath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var results = new List<WebProcessMetadataReadResult>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<WebProcessMetadata>(File.ReadAllText(path));
                results.Add(!IsValid(metadata)
                    ? WebProcessMetadataReadResult.Corrupt($"metadata '{path}' has an invalid schema")
                    : WebProcessMetadataReadResult.Found(metadata!));
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
            {
                results.Add(WebProcessMetadataReadResult.Corrupt($"metadata '{path}' could not be read: {ex.Message}"));
            }
        }

        return results;
    }

    public void Save(WebProcessMetadata metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
        var tempPath = $"{MetadataPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(tempPath, MetadataPath, overwrite: true);
            TryRestrictPermissions(MetadataPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public void DeleteIfOwned(string instanceId)
    {
        var result = Read();
        if (result.Metadata is not null &&
            string.Equals(result.Metadata.InstanceId, instanceId, StringComparison.Ordinal))
        {
            File.Delete(MetadataPath);
        }
    }

    public string? QuarantineCorrupt()
    {
        if (!File.Exists(MetadataPath))
        {
            return null;
        }

        var quarantineDirectory = Path.Combine(_actioHome, "web", "processes", "quarantine");
        Directory.CreateDirectory(quarantineDirectory);
        var target = Path.Combine(
            quarantineDirectory,
            $"{_urlKey}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        File.Move(MetadataPath, target);
        return target;
    }

    public void AppendLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            using var stream = File.Open(
                LogPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read);
            var currentLength = stream.Seek(0, SeekOrigin.End);
            var availableBytes = checked((int)Math.Min(
                int.MaxValue,
                MaximumLogBytes - currentLength));
            if (availableBytes <= 0)
            {
                return;
            }

            var entry = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
            var buffer = new byte[availableBytes];
            Encoding.UTF8.GetEncoder().Convert(
                entry.AsSpan(),
                buffer,
                flush: true,
                out _,
                out var bytesUsed,
                out _);
            stream.Write(buffer.AsSpan(0, bytesUsed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string? ReadLastLogLine()
    {
        try
        {
            return File.Exists(LogPath)
                ? File.ReadLines(LogPath).LastOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string CreateOwnershipToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public static WebOwnerState GetOwnerState(int processId, long expectedStartTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks
                ? WebOwnerState.Active
                : WebOwnerState.Stale;
        }
        catch (ArgumentException)
        {
            return WebOwnerState.Stale;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return WebOwnerState.Unknown;
        }
    }

    public static string CreateUrlKey(string url)
    {
        var normalized = url.Trim().TrimEnd('/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
        }
    }

    private static bool IsValid(WebProcessMetadata? metadata)
    {
        return metadata is not null &&
            metadata.SchemaVersion == SchemaVersion &&
            metadata.ProcessId > 0 &&
            metadata.ProcessStartTimeUtcTicks > 0 &&
            !string.IsNullOrWhiteSpace(metadata.InstanceId) &&
            !string.IsNullOrWhiteSpace(metadata.OwnershipToken) &&
            !string.IsNullOrWhiteSpace(metadata.RuntimeIdentity) &&
            !string.IsNullOrWhiteSpace(metadata.SnapshotPath) &&
            Path.IsPathFullyQualified(metadata.SnapshotPath) &&
            !string.IsNullOrWhiteSpace(metadata.HostPath) &&
            Path.IsPathFullyQualified(metadata.HostPath) &&
            !string.IsNullOrWhiteSpace(metadata.Url) &&
            Uri.TryCreate(metadata.Url, UriKind.Absolute, out var url) &&
            url.IsLoopback &&
            !string.IsNullOrWhiteSpace(metadata.ProjectRoot) &&
            Path.IsPathFullyQualified(metadata.ProjectRoot) &&
            !string.IsNullOrWhiteSpace(metadata.ActioHome) &&
            Path.IsPathFullyQualified(metadata.ActioHome);
    }
}

internal sealed record WebProcessMetadata(
    int SchemaVersion,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string InstanceId,
    string OwnershipToken,
    string RuntimeIdentity,
    string SnapshotPath,
    string HostPath,
    string Version,
    string Url,
    string ProjectRoot,
    string ActioHome,
    DateTimeOffset StartedAt)
{
    public static WebProcessMetadata Create(
        Process process,
        string instanceId,
        string ownershipToken,
        WebRuntimeSnapshot snapshot,
        string url,
        string projectRoot,
        string actioHome)
    {
        return new WebProcessMetadata(
            1,
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            instanceId,
            ownershipToken,
            snapshot.Identity,
            snapshot.RootPath,
            snapshot.HostPath,
            snapshot.Version,
            url,
            Path.GetFullPath(projectRoot),
            Path.GetFullPath(actioHome),
            DateTimeOffset.UtcNow);
    }
}

internal sealed record WebProcessMetadataReadResult(
    WebProcessMetadata? Metadata,
    bool IsCorrupt,
    string? Error)
{
    public static WebProcessMetadataReadResult Missing() => new(null, false, null);

    public static WebProcessMetadataReadResult Found(WebProcessMetadata metadata) => new(metadata, false, null);

    public static WebProcessMetadataReadResult Corrupt(string error) => new(null, true, error);
}

internal enum WebOwnerState
{
    Active,
    Stale,
    Unknown
}
