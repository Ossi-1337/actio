using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Actio.Engine.Actions;

namespace Actio.Storage;

public sealed class FileSystemActionCache : IActionCache, IGitHubActionSourceProvider
{
    private const string LocalKind = "local";
    private const string DockerKind = "docker";
    private const string GitHubKind = "github";
    private const string EntryFileName = "action.json";
    private const string SourceDirectoryName = "source";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IGitHubActionClient _githubClient;

    public FileSystemActionCache()
        : this(ActioHome.Resolve())
    {
    }

    public FileSystemActionCache(string actioHome, IGitHubActionClient? githubClient = null)
    {
        ActioHomePath = actioHome;
        _githubClient = githubClient ?? new GitHubActionClient();
    }

    public string ActioHomePath { get; }

    public string ActionCachePath => Path.Combine(Path.GetFullPath(ActioHomePath), "cache", "actions");

    public async Task<ActionCacheEntry> GetOrAddLocalActionAsync(
        LocalActionCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = CreateLocalKey(request.SourcePath, request.ContentHash);
        return await GetOrAddActionAsync(
            LocalKind,
            key,
            (cachePath, createdAt, now) => new ActionCacheEntry(
                key,
                LocalKind,
                request.Uses,
                request.SourcePath,
                request.ContentHash,
                cachePath,
                createdAt,
                now),
            cancellationToken);
    }

    public async Task<ActionCacheEntry> GetOrAddDockerImageActionAsync(
        DockerImageActionCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = CreateDockerKey(request.Image);
        return await GetOrAddActionAsync(
            DockerKind,
            key,
            (cachePath, createdAt, now) => new ActionCacheEntry(
                key,
                DockerKind,
                request.Uses,
                request.Image,
                request.Image,
                cachePath,
                createdAt,
                now,
                request.IsPinned ? request.Image : null,
                request.IsPinned ? null : request.MutablePart),
            cancellationToken);
    }

    public async Task<GitHubActionSourceResult> GetGitHubActionSourceAsync(
        GitHubActionSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var commitSha = request.IsPinned
                ? request.Ref
                : await _githubClient.ResolveCommitShaAsync(
                    request.Owner,
                    request.Repository,
                    request.Ref,
                    cancellationToken);
            var key = CreateGitHubKey(request.Owner, request.Repository, request.ActionPath, commitSha);
            var cachePath = Path.Combine(ActionCachePath, GitHubKind, key);
            var entryPath = Path.Combine(cachePath, EntryFileName);
            var now = DateTimeOffset.UtcNow;
            var createdAt = now;

            Directory.CreateDirectory(cachePath);

            var cachedEntry = await TryReadEntryAsync(entryPath, cancellationToken);
            if (cachedEntry is not null)
            {
                createdAt = cachedEntry.CreatedAt;
                if (File.Exists(cachedEntry.SourcePath))
                {
                    var refreshedEntry = CreateGitHubEntry(
                        request,
                        key,
                        commitSha,
                        cachedEntry.SourcePath,
                        cachePath,
                        createdAt,
                        now);
                    await WriteEntryAsync(entryPath, refreshedEntry, cancellationToken);
                    return GitHubActionSourceResult.Resolved(
                        cachedEntry.SourcePath,
                        Path.GetDirectoryName(cachedEntry.SourcePath)!,
                        refreshedEntry);
                }
            }

            var actionFilePath = await DownloadGitHubActionSourceAsync(
                request,
                commitSha,
                cachePath,
                cancellationToken);
            var entry = CreateGitHubEntry(request, key, commitSha, actionFilePath, cachePath, createdAt, now);
            await WriteEntryAsync(entryPath, entry, cancellationToken);

            return GitHubActionSourceResult.Resolved(
                actionFilePath,
                Path.GetDirectoryName(actionFilePath)!,
                entry);
        }
        catch (GitHubActionClientException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be downloaded: {ex.Message}"]);
        }
        catch (HttpRequestException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be downloaded: {ex.Message}"]);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be downloaded: {ex.Message}"]);
        }
        catch (JsonException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be downloaded: {ex.Message}"]);
        }
        catch (InvalidDataException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' archive could not be read: {ex.Message}"]);
        }
        catch (IOException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be cached: {ex.Message}"]);
        }
        catch (UnauthorizedAccessException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be cached: {ex.Message}"]);
        }
        catch (NotSupportedException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be cached: {ex.Message}"]);
        }
        catch (ArgumentException ex)
        {
            return GitHubActionSourceResult.Failed([$"uses '{request.Uses}' could not be cached: {ex.Message}"]);
        }
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

    private static string CreateDockerKey(string image)
    {
        var identity = string.Join('\0', DockerKind, image);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateGitHubKey(
        string owner,
        string repository,
        string actionPath,
        string commitSha)
    {
        var identity = string.Join(
            '\0',
            GitHubKind,
            owner.ToLowerInvariant(),
            repository.ToLowerInvariant(),
            NormalizeActionPath(actionPath),
            commitSha.ToLowerInvariant());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ActionCacheEntry CreateGitHubEntry(
        GitHubActionSourceRequest request,
        string key,
        string commitSha,
        string actionFilePath,
        string cachePath,
        DateTimeOffset createdAt,
        DateTimeOffset now)
    {
        return new ActionCacheEntry(
            key,
            GitHubKind,
            request.Uses,
            actionFilePath,
            commitSha,
            cachePath,
            createdAt,
            now,
            commitSha,
            request.IsPinned ? null : request.MutablePart);
    }

    private async Task<string> DownloadGitHubActionSourceAsync(
        GitHubActionSourceRequest request,
        string commitSha,
        string cachePath,
        CancellationToken cancellationToken)
    {
        var downloadPath = Path.Combine(cachePath, $"download-{Guid.NewGuid():N}");
        var extractPath = Path.Combine(downloadPath, "extract");
        var archivePath = Path.Combine(downloadPath, "archive.zip");
        var finalSourcePath = Path.Combine(cachePath, SourceDirectoryName);

        Directory.CreateDirectory(downloadPath);
        Directory.CreateDirectory(extractPath);

        try
        {
            await _githubClient.DownloadArchiveAsync(
                request.Owner,
                request.Repository,
                commitSha,
                archivePath,
                cancellationToken);

            ExtractZipSafely(archivePath, extractPath);
            var repoRoot = GetArchiveRootDirectory(extractPath);
            var actionDirectory = ResolveActionDirectory(repoRoot, request.ActionPath);
            var actionFilePath = ResolveActionFilePath(actionDirectory, request.Uses);
            var relativeActionFilePath = Path.GetRelativePath(repoRoot, actionFilePath);

            if (Directory.Exists(finalSourcePath))
            {
                Directory.Delete(finalSourcePath, recursive: true);
            }

            Directory.Move(repoRoot, finalSourcePath);
            return Path.Combine(finalSourcePath, relativeActionFilePath);
        }
        finally
        {
            TryDeleteDirectory(downloadPath);
        }
    }

    private static void ExtractZipSafely(string archivePath, string destinationPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var destinationRoot = Path.GetFullPath(destinationPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        foreach (var entry in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!IsUnderRoot(targetPath, destinationRoot))
            {
                throw new InvalidDataException("Archive contains a file outside the expected extraction directory.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static string GetArchiveRootDirectory(string extractPath)
    {
        var roots = Directory.GetDirectories(extractPath);
        if (roots.Length != 1)
        {
            throw new InvalidDataException("GitHub archive did not contain the expected single repository root directory.");
        }

        return roots[0];
    }

    private static string ResolveActionDirectory(string repoRoot, string actionPath)
    {
        var normalizedActionPath = NormalizeActionPath(actionPath);
        var actionPathParts = normalizedActionPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (actionPathParts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("GitHub action path contains an invalid path segment.");
        }

        var actionDirectory = string.IsNullOrEmpty(normalizedActionPath)
            ? repoRoot
            : Path.GetFullPath(actionPathParts.Aggregate(repoRoot, Path.Combine));

        if (!IsUnderRoot(actionDirectory, repoRoot))
        {
            throw new InvalidDataException("GitHub action path escaped the downloaded repository root.");
        }

        if (!Directory.Exists(actionDirectory))
        {
            throw new InvalidDataException($"GitHub action path '{normalizedActionPath}' was not found in the downloaded repository.");
        }

        return actionDirectory;
    }

    private static string ResolveActionFilePath(string actionDirectory, string uses)
    {
        var ymlPath = Path.Combine(actionDirectory, "action.yml");
        if (File.Exists(ymlPath))
        {
            return ymlPath;
        }

        var yamlPath = Path.Combine(actionDirectory, "action.yaml");
        if (File.Exists(yamlPath))
        {
            return yamlPath;
        }

        throw new InvalidDataException($"uses '{uses}' did not contain an action.yml or action.yaml file.");
    }

    private static string NormalizeActionPath(string actionPath)
    {
        return actionPath.Replace('\\', '/').Trim('/');
    }

    private async Task<ActionCacheEntry> GetOrAddActionAsync(
        string kind,
        string key,
        Func<string, DateTimeOffset, DateTimeOffset, ActionCacheEntry> createEntry,
        CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ActionCachePath, kind, key);
        var entryPath = Path.Combine(cachePath, EntryFileName);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now;

        Directory.CreateDirectory(cachePath);

        if (File.Exists(entryPath))
        {
            createdAt = await ReadCreatedAtAsync(entryPath, now, cancellationToken);
        }

        var entry = createEntry(cachePath, createdAt, now);
        await using var writeStream = File.Create(entryPath);
        await JsonSerializer.SerializeAsync(writeStream, entry, JsonOptions, cancellationToken);
        return entry;
    }

    private static async Task WriteEntryAsync(
        string entryPath,
        ActionCacheEntry entry,
        CancellationToken cancellationToken)
    {
        await using var writeStream = File.Create(entryPath);
        await JsonSerializer.SerializeAsync(writeStream, entry, JsonOptions, cancellationToken);
    }

    private static async Task<ActionCacheEntry?> TryReadEntryAsync(
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(entryPath))
        {
            return null;
        }

        try
        {
            await using var readStream = File.OpenRead(entryPath);
            return await JsonSerializer.DeserializeAsync<ActionCacheEntry>(
                readStream,
                JsonOptions,
                cancellationToken);
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

        return null;
    }

    private static async Task<DateTimeOffset> ReadCreatedAtAsync(
        string entryPath,
        DateTimeOffset fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var readStream = File.OpenRead(entryPath);
            var existing = await JsonSerializer.DeserializeAsync<ActionCacheEntry>(
                readStream,
                JsonOptions,
                cancellationToken);
            return existing?.CreatedAt ?? fallback;
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

        return fallback;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
}
