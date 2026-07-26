using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Actio.Cli;

internal sealed class WebRuntimeSnapshotManager
{
    internal const string ManifestFileName = "runtime.json";
    private const int ManifestSchemaVersion = 1;
    private readonly string _sourceRoot;
    private readonly string _processPath;
    private readonly string _entryAssemblyPath;
    private readonly string _version;

    public WebRuntimeSnapshotManager(
        string sourceRoot,
        string processPath,
        string entryAssemblyPath,
        string version)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _processPath = Path.GetFullPath(processPath);
        _entryAssemblyPath = Path.GetFullPath(entryAssemblyPath);
        _version = version;
    }

    public static WebRuntimeSnapshotManager CreateCurrent()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var entryAssemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            throw new InvalidOperationException("Current entry assembly path is unavailable.");
        }

        return new WebRuntimeSnapshotManager(
            AppContext.BaseDirectory,
            processPath,
            entryAssemblyPath,
            CliVersion.GetVersion());
    }

    public WebRuntimeDescription DescribeCurrent(CancellationToken cancellationToken = default)
    {
        var files = ReadSourceFiles(cancellationToken);
        var entryPath = GetRequiredRelativePath(_entryAssemblyPath, "entry assembly");
        var usesDotnetHost = IsDotnetHost(_processPath);
        var appHostPath = usesDotnetHost
            ? null
            : GetRequiredRelativePath(_processPath, "application host");
        var identity = ComputeIdentity(files, entryPath, appHostPath, usesDotnetHost, _version);

        return new WebRuntimeDescription(
            identity,
            entryPath,
            appHostPath,
            usesDotnetHost,
            _version,
            files);
    }

    public WebRuntimeSnapshot Prepare(
        string actioHome,
        CancellationToken cancellationToken = default)
    {
        var runtimeRoot = Path.Combine(Path.GetFullPath(actioHome), "web", "runtimes");
        if (IsPathInside(runtimeRoot, _sourceRoot))
        {
            throw new InvalidOperationException(
                $"Web runtime snapshot root '{runtimeRoot}' cannot be inside runtime source '{_sourceRoot}'.");
        }

        var description = DescribeCurrent(cancellationToken);
        Directory.CreateDirectory(runtimeRoot);

        var snapshotPath = Path.Combine(runtimeRoot, description.Identity);
        if (TryValidate(snapshotPath, description))
        {
            return CreateSnapshot(snapshotPath, description);
        }

        var stagingPath = Path.Combine(runtimeRoot, $".staging-{description.Identity}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingPath);
            CopyFiles(stagingPath, description.Files, cancellationToken);
            WriteManifest(stagingPath, description);

            if (Directory.Exists(snapshotPath))
            {
                if (!TryValidate(snapshotPath, description))
                {
                    throw new IOException(
                        $"Web runtime snapshot '{snapshotPath}' exists but does not match its content identity.");
                }
            }
            else
            {
                try
                {
                    Directory.Move(stagingPath, snapshotPath);
                }
                catch (IOException) when (Directory.Exists(snapshotPath) && TryValidate(snapshotPath, description))
                {
                }
            }
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }

        if (!TryValidate(snapshotPath, description))
        {
            throw new IOException($"Web runtime snapshot '{snapshotPath}' could not be validated after creation.");
        }

        return CreateSnapshot(snapshotPath, description);
    }

    public WebSnapshotCleanupResult Cleanup(
        string actioHome,
        string currentIdentity,
        IReadOnlySet<string> protectedIdentities,
        TimeSpan startupGrace)
    {
        var runtimeRoot = Path.Combine(Path.GetFullPath(actioHome), "web", "runtimes");
        if (!Directory.Exists(runtimeRoot))
        {
            return new WebSnapshotCleanupResult(0, []);
        }

        var removed = 0;
        var warnings = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".staging-", StringComparison.Ordinal))
            {
                try
                {
                    if (DateTimeOffset.UtcNow - Directory.GetCreationTimeUtc(directory) >= startupGrace)
                    {
                        TryDeleteDirectory(directory, warnings, ref removed);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Web runtime staging directory '{directory}' age could not be verified: {ex.Message}");
                }

                continue;
            }

            if (string.Equals(name, currentIdentity, StringComparison.Ordinal) ||
                protectedIdentities.Contains(name))
            {
                continue;
            }

            try
            {
                if (DateTimeOffset.UtcNow - Directory.GetCreationTimeUtc(directory) < startupGrace)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Web runtime snapshot '{directory}' age could not be verified: {ex.Message}");
                continue;
            }

            var lockPath = WebProcessMetadataStore.GetRuntimeLockPath(actioHome, name);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                using var runtimeLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"Web runtime snapshot '{directory}' could not be removed: {ex.Message}");
            }
        }

        return new WebSnapshotCleanupResult(removed, warnings);
    }

    private WebRuntimeSnapshot CreateSnapshot(string snapshotPath, WebRuntimeDescription description)
    {
        return new WebRuntimeSnapshot(
            description.Identity,
            snapshotPath,
            Path.Combine(snapshotPath, FromManifestPath(description.EntryPath)),
            description.AppHostPath is null
                ? _processPath
                : Path.Combine(snapshotPath, FromManifestPath(description.AppHostPath)),
            description.UsesDotnetHost,
            description.Version);
    }

    private IReadOnlyList<WebRuntimeFile> ReadSourceFiles(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sourceRoot))
        {
            throw new DirectoryNotFoundException($"Runtime source directory '{_sourceRoot}' does not exist.");
        }

        var files = new List<WebRuntimeFile>();
        var pending = new Stack<string>();
        pending.Push(_sourceRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            EnsureNotReparsePoint(directory, "directory");

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                EnsureNotReparsePoint(childDirectory, "directory");
                pending.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotReparsePoint(file, "file");
                var relativePath = ToManifestPath(Path.GetRelativePath(_sourceRoot, file));
                if (string.Equals(relativePath, ManifestFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                var info = new FileInfo(file);
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                var unixMode = OperatingSystem.IsWindows()
                    ? null
                    : (int?)File.GetUnixFileMode(file);
                files.Add(new WebRuntimeFile(relativePath, info.Length, hash, unixMode));
            }
        }

        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static string ComputeIdentity(
        IReadOnlyList<WebRuntimeFile> files,
        string entryPath,
        string? appHostPath,
        bool usesDotnetHost,
        string version)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, ManifestSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, version);
        Append(hash, entryPath);
        Append(hash, appHostPath ?? string.Empty);
        Append(hash, usesDotnetHost ? "dotnet" : "apphost");

        foreach (var file in files)
        {
            Append(hash, file.Path);
            Append(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, file.Sha256);
            Append(hash, file.UnixMode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private bool TryValidate(string snapshotPath, WebRuntimeDescription expected)
    {
        try
        {
            EnsureNotReparsePoint(snapshotPath, "directory");
            var manifestPath = Path.Combine(snapshotPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            EnsureNotReparsePoint(manifestPath, "file");
            var manifest = JsonSerializer.Deserialize<WebRuntimeManifest>(File.ReadAllText(manifestPath));
            if (manifest is null ||
                manifest.SchemaVersion != ManifestSchemaVersion ||
                !string.Equals(manifest.Identity, expected.Identity, StringComparison.Ordinal) ||
                !string.Equals(manifest.EntryPath, expected.EntryPath, StringComparison.Ordinal) ||
                !string.Equals(manifest.AppHostPath, expected.AppHostPath, StringComparison.Ordinal) ||
                manifest.UsesDotnetHost != expected.UsesDotnetHost ||
                !string.Equals(manifest.Version, expected.Version, StringComparison.Ordinal) ||
                manifest.Files is null ||
                manifest.Files.Count != expected.Files.Count)
            {
                return false;
            }

            var actualPaths = ReadSnapshotPaths(snapshotPath);
            var expectedPaths = expected.Files
                .Select(file => file.Path)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (!actualPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal))
            {
                return false;
            }

            for (var index = 0; index < expected.Files.Count; index++)
            {
                var expectedFile = expected.Files[index];
                var actualFile = manifest.Files[index];
                if (actualFile != expectedFile)
                {
                    return false;
                }

                var filePath = Path.Combine(snapshotPath, FromManifestPath(actualFile.Path));
                if (!File.Exists(filePath) || new FileInfo(filePath).Length != actualFile.Length)
                {
                    return false;
                }

                using var stream = File.OpenRead(filePath);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actualHash, actualFile.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadSnapshotPaths(string snapshotPath)
    {
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(snapshotPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureNotReparsePoint(directory, "directory");
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                EnsureNotReparsePoint(childDirectory, "directory");
                pending.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                EnsureNotReparsePoint(file, "file");
                var relativePath = ToManifestPath(Path.GetRelativePath(snapshotPath, file));
                if (!string.Equals(relativePath, ManifestFileName, StringComparison.Ordinal))
                {
                    paths.Add(relativePath);
                }
            }
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private void CopyFiles(
        string stagingPath,
        IReadOnlyList<WebRuntimeFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(_sourceRoot, FromManifestPath(file.Path));
            var targetPath = Path.Combine(stagingPath, FromManifestPath(file.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: false);
            if (!OperatingSystem.IsWindows() && file.UnixMode is not null)
            {
                File.SetUnixFileMode(targetPath, (UnixFileMode)file.UnixMode.Value);
            }
        }
    }

    private static void WriteManifest(string stagingPath, WebRuntimeDescription description)
    {
        var manifest = new WebRuntimeManifest(
            ManifestSchemaVersion,
            description.Identity,
            description.Version,
            description.EntryPath,
            description.AppHostPath,
            description.UsesDotnetHost,
            description.Files);
        var path = Path.Combine(stagingPath, ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private string GetRequiredRelativePath(string path, string kind)
    {
        var relativePath = Path.GetRelativePath(_sourceRoot, path);
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Current {kind} '{path}' must stay inside runtime source '{_sourceRoot}'.");
        }

        return ToManifestPath(relativePath);
    }

    private static void EnsureNotReparsePoint(string path, string kind)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Runtime source {kind} '{path}' cannot be a symlink or reparse point.");
        }
    }

    private static bool IsDotnetHost(string processPath)
    {
        return string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ToManifestPath(string path) => path.Replace('\\', '/');

    private static string FromManifestPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static bool IsPathInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, comparison) ||
            fullPath.StartsWith($"{fullRoot}{Path.DirectorySeparatorChar}", comparison);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void TryDeleteDirectory(string path, List<string> warnings, ref int removed)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            removed++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Web runtime staging directory '{path}' could not be removed: {ex.Message}");
        }
    }

    private sealed record WebRuntimeManifest(
        int SchemaVersion,
        string Identity,
        string Version,
        string EntryPath,
        string? AppHostPath,
        bool UsesDotnetHost,
        IReadOnlyList<WebRuntimeFile> Files);
}

internal sealed record WebRuntimeDescription(
    string Identity,
    string EntryPath,
    string? AppHostPath,
    bool UsesDotnetHost,
    string Version,
    IReadOnlyList<WebRuntimeFile> Files);

internal sealed record WebRuntimeFile(
    string Path,
    long Length,
    string Sha256,
    int? UnixMode);

internal sealed record WebRuntimeSnapshot(
    string Identity,
    string RootPath,
    string EntryAssemblyPath,
    string HostPath,
    bool UsesDotnetHost,
    string Version);

internal sealed record WebSnapshotCleanupResult(
    int Removed,
    IReadOnlyList<string> Warnings);
