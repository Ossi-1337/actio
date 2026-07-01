using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Actio.Engine.Caching;

namespace Actio.Storage;

public sealed class FileSystemDependencyCache : IDependencyCache
{
    private const string EntryFileName = "cache.json";
    private const string ContentDirectoryName = "content";
    private const string VersionPrefix = "actio-dependency-cache-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FileSystemDependencyCache()
        : this(ActioHome.Resolve())
    {
    }

    public FileSystemDependencyCache(string actioHome)
    {
        ActioHomePath = actioHome;
    }

    public string ActioHomePath { get; }

    public string DependencyCachePath => Path.Combine(Path.GetFullPath(ActioHomePath), "cache", "dependencies");

    public async Task<DependencyCacheRestoreResult> RestoreAsync(
        DependencyCacheRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var pathResolution = ResolveCachePaths(request.ProjectRoot, request.Paths);
        if (!pathResolution.Success)
        {
            return DependencyCacheRestoreResult.Failed(pathResolution.Errors);
        }

        var version = CreateVersion(pathResolution.Paths.Select(path => path.RelativePath));
        var match = await FindMatchAsync(request.Key, request.RestoreKeys, version, cancellationToken);
        if (match.Entry is null)
        {
            return DependencyCacheRestoreResult.Miss();
        }

        try
        {
            RestoreEntry(request.ProjectRoot, match.Entry);
            var refreshedEntry = match.Entry with { LastUsedAt = DateTimeOffset.UtcNow };
            await WriteEntryAsync(GetEntryPath(refreshedEntry.CachePath), refreshedEntry, cancellationToken);
            return DependencyCacheRestoreResult.Restored(refreshedEntry, match.ExactMatch, match.RestoreKey);
        }
        catch (Exception ex) when (IsRecoverableFileError(ex))
        {
            return DependencyCacheRestoreResult.Failed([$"dependency cache '{match.Entry.Key}' could not be restored: {ex.Message}"]);
        }
    }

    public async Task<DependencyCacheSaveResult> SaveAsync(
        DependencyCacheSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var pathResolution = ResolveCachePaths(request.ProjectRoot, request.Paths);
        if (!pathResolution.Success)
        {
            return DependencyCacheSaveResult.Failed(pathResolution.Errors);
        }

        var storageErrors = ValidateDoesNotIncludeCacheStorage(pathResolution.Paths);
        if (storageErrors.Count > 0)
        {
            return DependencyCacheSaveResult.Failed(storageErrors);
        }

        var relativePaths = pathResolution.Paths.Select(path => path.RelativePath).ToArray();
        var version = CreateVersion(relativePaths);
        var cachePath = GetCachePath(request.Key, version);
        var entryPath = GetEntryPath(cachePath);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var existingEntry = await TryReadEntryAsync(entryPath, cancellationToken);
            if (existingEntry is not null)
            {
                var refreshedEntry = existingEntry with { LastUsedAt = now };
                await WriteEntryAsync(entryPath, refreshedEntry, cancellationToken);
                return DependencyCacheSaveResult.Skipped([$"Dependency cache '{request.Key}' already exists."], refreshedEntry);
            }

            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, recursive: true);
            }

            var tempPath = Path.Combine(DependencyCachePath, $".tmp-{Guid.NewGuid():N}");
            var tempContentPath = Path.Combine(tempPath, ContentDirectoryName);
            Directory.CreateDirectory(tempContentPath);

            try
            {
                var savedPaths = SaveExistingPaths(pathResolution.Paths, tempContentPath);
                if (savedPaths.Count == 0)
                {
                    return DependencyCacheSaveResult.Skipped(["No dependency cache paths exist; skipping save."]);
                }

                var entry = new DependencyCacheEntry(
                    request.Key,
                    version,
                    savedPaths,
                    cachePath,
                    now,
                    now);
                await WriteEntryAsync(Path.Combine(tempPath, EntryFileName), entry, cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                Directory.Move(tempPath, cachePath);
                return DependencyCacheSaveResult.SavedEntry(entry);
            }
            finally
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, recursive: true);
                }
            }
        }
        catch (Exception ex) when (IsRecoverableFileError(ex))
        {
            return DependencyCacheSaveResult.Failed([$"dependency cache '{request.Key}' could not be saved: {ex.Message}"]);
        }
    }

    public async Task<IReadOnlyList<DependencyCacheEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DependencyCachePath))
        {
            return [];
        }

        var entries = new List<DependencyCacheEntry>();
        string[] entryPaths;
        try
        {
            entryPaths = Directory.EnumerateFiles(DependencyCachePath, EntryFileName, SearchOption.AllDirectories).ToArray();
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
            var entry = await TryReadEntryAsync(entryPath, cancellationToken);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries
            .OrderByDescending(entry => entry.LastUsedAt)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<int> CleanAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DependencyCachePath))
        {
            return Task.FromResult(0);
        }

        var count = Directory
            .EnumerateFiles(DependencyCachePath, EntryFileName, SearchOption.AllDirectories)
            .Count();

        Directory.Delete(DependencyCachePath, recursive: true);
        return Task.FromResult(count);
    }

    private async Task<CacheMatch> FindMatchAsync(
        string key,
        IReadOnlyList<string> restoreKeys,
        string version,
        CancellationToken cancellationToken)
    {
        var exactEntry = await TryReadEntryAsync(GetEntryPath(GetCachePath(key, version)), cancellationToken);
        if (exactEntry is not null)
        {
            return new CacheMatch(exactEntry, true, null);
        }

        if (restoreKeys.Count == 0 || !Directory.Exists(DependencyCachePath))
        {
            return CacheMatch.None;
        }

        var entries = await ListAsync(cancellationToken);
        foreach (var restoreKey in restoreKeys)
        {
            var entry = entries.FirstOrDefault(item =>
                string.Equals(item.Version, version, StringComparison.Ordinal) &&
                item.Key.StartsWith(restoreKey, StringComparison.Ordinal));
            if (entry is not null)
            {
                return new CacheMatch(entry, false, restoreKey);
            }
        }

        return CacheMatch.None;
    }

    private static IReadOnlyList<string> SaveExistingPaths(
        IReadOnlyList<ResolvedCachePath> paths,
        string contentPath)
    {
        var savedPaths = new List<string>();

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            if (File.Exists(path.FullPath))
            {
                var targetDirectory = Path.Combine(contentPath, index.ToString(), "file");
                Directory.CreateDirectory(targetDirectory);
                File.Copy(path.FullPath, Path.Combine(targetDirectory, Path.GetFileName(path.FullPath)), overwrite: true);
                savedPaths.Add(path.RelativePath);
                continue;
            }

            if (Directory.Exists(path.FullPath))
            {
                CopyDirectory(path.FullPath, Path.Combine(contentPath, index.ToString(), "directory"));
                savedPaths.Add(path.RelativePath);
            }
        }

        return savedPaths;
    }

    private static void RestoreEntry(string projectRoot, DependencyCacheEntry entry)
    {
        var contentPath = Path.Combine(entry.CachePath, ContentDirectoryName);
        for (var index = 0; index < entry.Paths.Count; index++)
        {
            var sourcePath = Path.Combine(contentPath, index.ToString());
            if (!Directory.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = ResolveWorkspacePath(projectRoot, entry.Paths[index]);
            if (!IsUnderRoot(targetPath, Path.GetFullPath(projectRoot)))
            {
                throw new InvalidOperationException($"dependency cache path '{entry.Paths[index]}' must stay inside the workspace.");
            }

            var filePath = Path.Combine(sourcePath, "file");
            if (Directory.Exists(filePath))
            {
                var sourceEntries = Directory.EnumerateFiles(filePath).ToArray();
                if (sourceEntries.Length != 1)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourceEntries[0], targetPath, overwrite: true);
                continue;
            }

            var directoryPath = Path.Combine(sourcePath, "directory");
            if (Directory.Exists(directoryPath))
            {
                CopyDirectory(directoryPath, targetPath);
            }
        }
    }

    private static PathResolution ResolveCachePaths(string projectRoot, IReadOnlyList<string> paths)
    {
        var resolvedPaths = new List<ResolvedCachePath>();
        var errors = new List<string>();
        var fullProjectRoot = Path.GetFullPath(projectRoot);

        foreach (var item in paths)
        {
            var normalized = NormalizeRelativePath(item);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (ContainsUnsupportedPattern(normalized))
            {
                errors.Add($"dependency cache path '{item}' cannot contain glob wildcards yet.");
                continue;
            }

            var fullPath = ResolveWorkspacePath(fullProjectRoot, normalized);
            if (!IsUnderRoot(fullPath, fullProjectRoot))
            {
                errors.Add($"dependency cache path '{item}' must stay inside the workspace.");
                continue;
            }

            resolvedPaths.Add(new ResolvedCachePath(normalized, fullPath));
        }

        if (resolvedPaths.Count == 0 && errors.Count == 0)
        {
            errors.Add("dependency cache path is required.");
        }

        return errors.Count == 0
            ? PathResolution.Resolved(resolvedPaths)
            : PathResolution.Failed(errors);
    }

    private IReadOnlyList<string> ValidateDoesNotIncludeCacheStorage(IReadOnlyList<ResolvedCachePath> paths)
    {
        var fullCachePath = Path.GetFullPath(DependencyCachePath);
        return paths
            .Where(path => IsUnderRoot(fullCachePath, path.FullPath) || IsUnderRoot(path.FullPath, fullCachePath))
            .Select(path => $"dependency cache path '{path.RelativePath}' cannot include Actio dependency cache storage.")
            .ToArray();
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    private static bool ContainsUnsupportedPattern(string path)
        => path.Contains('*', StringComparison.Ordinal) ||
            path.Contains('?', StringComparison.Ordinal) ||
            path.Contains('[', StringComparison.Ordinal);

    private static string ResolveWorkspacePath(string projectRoot, string relativePath)
    {
        var path = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(projectRoot, path));
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }

    private string GetCachePath(string key, string version)
        => Path.Combine(DependencyCachePath, CreateCacheId(key, version));

    private static string GetEntryPath(string cachePath)
        => Path.Combine(cachePath, EntryFileName);

    private static string CreateCacheId(string key, string version)
    {
        var identity = string.Join('\0', key, version);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateVersion(IEnumerable<string> paths)
    {
        var identity = string.Join('\0', [VersionPrefix, .. paths.OrderBy(path => path, StringComparer.Ordinal)]);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteEntryAsync(
        string entryPath,
        DependencyCacheEntry entry,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        await using var stream = File.Create(entryPath);
        await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, cancellationToken);
    }

    private static async Task<DependencyCacheEntry?> TryReadEntryAsync(
        string entryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(entryPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(entryPath);
            return await JsonSerializer.DeserializeAsync<DependencyCacheEntry>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);

        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsRecoverableFileError(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException;

    private sealed record CacheMatch(
        DependencyCacheEntry? Entry,
        bool ExactMatch,
        string? RestoreKey)
    {
        public static CacheMatch None { get; } = new(null, false, null);
    }

    private sealed record ResolvedCachePath(string RelativePath, string FullPath);

    private sealed record PathResolution(
        bool Success,
        IReadOnlyList<ResolvedCachePath> Paths,
        IReadOnlyList<string> Errors)
    {
        public static PathResolution Resolved(IReadOnlyList<ResolvedCachePath> paths)
            => new(true, paths, []);

        public static PathResolution Failed(IReadOnlyList<string> errors)
            => new(false, [], errors);
    }
}
