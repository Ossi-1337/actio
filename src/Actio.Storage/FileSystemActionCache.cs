using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Actio.Engine.Actions;

namespace Actio.Storage;

public sealed class FileSystemActionCache : IActionCache
{
    private const string LocalKind = "local";
    private const string EntryFileName = "action.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FileSystemActionCache()
        : this(ActioHome.Resolve())
    {
    }

    public FileSystemActionCache(string actioHome)
    {
        ActioHomePath = actioHome;
    }

    public string ActioHomePath { get; }

    public string ActionCachePath => Path.Combine(Path.GetFullPath(ActioHomePath), "cache", "actions");

    public async Task<ActionCacheEntry> GetOrAddLocalActionAsync(
        LocalActionCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = CreateLocalKey(request.SourcePath, request.ContentHash);
        var cachePath = Path.Combine(ActionCachePath, LocalKind, key);
        var entryPath = Path.Combine(cachePath, EntryFileName);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now;

        Directory.CreateDirectory(cachePath);

        if (File.Exists(entryPath))
        {
            try
            {
                await using var readStream = File.OpenRead(entryPath);
                var existing = await JsonSerializer.DeserializeAsync<ActionCacheEntry>(
                    readStream,
                    JsonOptions,
                    cancellationToken);
                createdAt = existing?.CreatedAt ?? now;
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var entry = new ActionCacheEntry(
            key,
            LocalKind,
            request.Uses,
            request.SourcePath,
            request.ContentHash,
            cachePath,
            createdAt,
            now);

        await using var writeStream = File.Create(entryPath);
        await JsonSerializer.SerializeAsync(writeStream, entry, JsonOptions, cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<ActionCacheEntry>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ActionCachePath))
        {
            return [];
        }

        var entries = new List<ActionCacheEntry>();

        string[] entryPaths;
        try
        {
            entryPaths = Directory.EnumerateFiles(ActionCachePath, EntryFileName, SearchOption.AllDirectories).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var entryPath in entryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = File.OpenRead(entryPath);
                var entry = await JsonSerializer.DeserializeAsync<ActionCacheEntry>(stream, JsonOptions, cancellationToken);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return entries
            .OrderByDescending(entry => entry.LastUsedAt)
            .ThenBy(entry => entry.Uses, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<int> CleanAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ActionCachePath))
        {
            return Task.FromResult(0);
        }

        var count = Directory
            .EnumerateFiles(ActionCachePath, EntryFileName, SearchOption.AllDirectories)
            .Count();

        Directory.Delete(ActionCachePath, recursive: true);
        return Task.FromResult(count);
    }

    private static string CreateLocalKey(string sourcePath, string contentHash)
    {
        var normalizedPath = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        var identity = string.Join('\0', LocalKind, normalizedPath, contentHash);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
