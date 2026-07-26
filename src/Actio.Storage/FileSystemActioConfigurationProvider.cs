using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Actio.Engine.Configuration;
using Actio.Engine.Execution;

namespace Actio.Storage;

public sealed class FileSystemActioConfigurationProvider : IActioConfigurationProvider
{
    private readonly string _actioHome;

    public FileSystemActioConfigurationProvider()
        : this(ActioHome.Resolve())
    {
    }

    public FileSystemActioConfigurationProvider(string actioHome)
    {
        _actioHome = Path.GetFullPath(actioHome);
    }

    public ActioConfigurationLoadResult Load()
    {
        var validation = Validate();
        if (!validation.Success)
        {
            return Failed(validation.Errors);
        }

        try
        {
            Directory.CreateDirectory(_actioHome);
            using var process = Process.GetCurrentProcess();
            return new(
                true,
                validation.Configuration,
                new ActioInstanceIdentity(
                    LoadOrCreateInstanceId(),
                    Environment.ProcessId,
                    process.StartTime.ToUniversalTime().Ticks));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return Failed([$"Actio configuration could not be loaded: {ex.Message}"]);
        }
    }

    public ActioConfigurationValidationResult Validate()
    {
        try
        {
            var configuration = LoadConfiguration();
            var errors = ValidateConfiguration(configuration);
            return errors.Count == 0
                ? new ActioConfigurationValidationResult(true, configuration)
                : new ActioConfigurationValidationResult(false, new ContainerResourceConfiguration(), errors);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return new ActioConfigurationValidationResult(
                false,
                new ContainerResourceConfiguration(),
                [$"Actio configuration could not be loaded: {ex.Message}"]);
        }
    }

    private ContainerResourceConfiguration LoadConfiguration()
    {
        var path = Path.Combine(_actioHome, "config.json");
        if (!File.Exists(path))
        {
            return new ContainerResourceConfiguration();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var document = JsonSerializer.Deserialize<ActioConfigurationDocument>(
            File.ReadAllText(path),
            options) ?? new ActioConfigurationDocument();
        return document.Resources ?? new ContainerResourceConfiguration();
    }

    private string LoadOrCreateInstanceId()
    {
        var path = Path.Combine(_actioHome, "instance-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _))
            {
                return existing;
            }

            throw new InvalidDataException($"Actio instance id at '{path}' is invalid.");
        }

        var instanceId = new Guid(RandomNumberGenerator.GetBytes(16)).ToString("N");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(instanceId);
            return instanceId;
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            return Guid.TryParse(existing, out _)
                ? existing
                : throw new InvalidDataException($"Actio instance id at '{path}' is invalid.");
        }
    }

    private static IReadOnlyList<string> ValidateConfiguration(ContainerResourceConfiguration value)
    {
        var errors = new List<string>();
        AddRangeError(errors, value.Cpu, "resources.cpu", 0.25, 8);
        AddRangeError(errors, value.MemoryMiB, "resources.memoryMiB", 256L, 16384L);
        AddRangeError(errors, value.Pids, "resources.pids", 64, 4096);
        AddRangeError(errors, value.TempMiB, "resources.tempMiB", 64L, 2048L);
        AddRangeError(errors, value.DockerLogMiB, "resources.dockerLogMiB", 1L, 100L);
        AddRangeError(errors, value.DockerLogFiles, "resources.dockerLogFiles", 1, 5);
        AddRangeError(errors, value.StepLogMiB, "resources.stepLogMiB", 1L, 100L);
        return errors;
    }

    private static void AddRangeError<T>(
        List<string> errors,
        T? value,
        string path,
        T minimum,
        T maximum)
        where T : struct, IComparable<T>
    {
        if (value is not null &&
            (value.Value.CompareTo(minimum) < 0 || value.Value.CompareTo(maximum) > 0))
        {
            errors.Add(
                $"ACTIO_HOME/config.json '{path}' must be between {minimum} and {maximum}.");
        }
    }

    private static ActioConfigurationLoadResult Failed(IReadOnlyList<string> errors)
        => new(
            false,
            new ContainerResourceConfiguration(),
            new ActioInstanceIdentity("unavailable", Environment.ProcessId, 0),
            errors);

    private sealed record ActioConfigurationDocument(ContainerResourceConfiguration? Resources = null);
}
