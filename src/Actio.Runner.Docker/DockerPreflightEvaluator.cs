using System.Globalization;
using System.Text.Json;
using Actio.Engine.Execution;
using Actio.Engine.Runs;

namespace Actio.Runner.Docker;

internal static class DockerPreflightEvaluator
{
    internal static DockerPreflightEvaluation Evaluate(
        string json,
        RunnerPreflightRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var engineVersion = ReadString(root, "ServerVersion");
            var operatingSystem = ReadString(root, "OperatingSystem");
            var osType = ReadString(root, "OSType");
            var cpuCapacity = ReadDouble(root, "NCPU");
            var memoryCapacity = ReadLong(root, "MemTotal");
            var cpuSupported = ReadBoolean(root, "CpuCfsQuota", defaultValue: cpuCapacity > 0);
            var memorySupported = ReadBoolean(root, "MemoryLimit", defaultValue: memoryCapacity > 0);
            var swapSupported = ReadBoolean(root, "SwapLimit", defaultValue: false);
            var pidsSupported = ReadBoolean(root, "PidsLimit", defaultValue: false);
            var cgroupVersion = ReadString(root, "CgroupVersion");
            var cgroupDriver = ReadString(root, "CgroupDriver");
            var securityOptions = ReadStrings(root, "SecurityOptions");
            var seccomp = securityOptions.Any(item =>
                item.Contains("seccomp", StringComparison.OrdinalIgnoreCase));
            var rootless = securityOptions.Any(item =>
                item.Contains("rootless", StringComparison.OrdinalIgnoreCase));
            var userNamespace = securityOptions.Any(item =>
                item.Contains("userns", StringComparison.OrdinalIgnoreCase));
            var desktop = operatingSystem.Contains("Docker Desktop", StringComparison.OrdinalIgnoreCase);
            var logDrivers = ReadNestedStrings(root, "Plugins", "Log");
            var localLoggingSupported = logDrivers.Contains("local", StringComparer.Ordinal);
            var errors = new List<string>();
            var warnings = new List<string>();

            if (!string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Docker preflight requires Linux container mode.");
            }

            if (!seccomp)
            {
                errors.Add("Docker preflight could not verify seccomp support.");
            }

            if (!cpuSupported || cpuCapacity <= 0)
            {
                errors.Add("Docker preflight requires CPU limit support.");
            }

            if (!memorySupported || memoryCapacity <= 0)
            {
                errors.Add("Docker preflight requires memory limit support.");
            }

            if (!pidsSupported)
            {
                errors.Add("Docker preflight requires PID limit support.");
            }

            if (!localLoggingSupported)
            {
                errors.Add("Docker preflight requires the local logging driver for bounded container logs.");
            }

            var strict = string.Equals(
                request.Policy.RequestedProfile,
                RunnerSecurityProfiles.Strict,
                StringComparison.Ordinal);
            if (!swapSupported)
            {
                if (strict)
                {
                    errors.Add("Strict profile requires Docker swap-limit support.");
                }
                else
                {
                    warnings.Add("secure-baseline: Docker swap-limit support was not verified.");
                }
            }

            if (strict && !desktop && !rootless && !userNamespace)
            {
                errors.Add("Strict profile on native Linux requires rootless Docker or userns-remap.");
            }
            else if (!strict && !desktop && !rootless && !userNamespace)
            {
                warnings.Add("secure-baseline: rootful native Linux daemon detected.");
            }

            if (request.HasPublishedPorts &&
                (!Version.TryParse(engineVersion.Split('-', 2)[0], out var version) || version.Major < 28))
            {
                errors.Add("Published ports require Docker Engine 28 or newer.");
            }

            var defaults = ContainerResourceLimits.Defaults;
            var configured = request.Policy.ResourceConfiguration;
            var requestedCpu = configured.Cpu ?? defaults.Cpu;
            var memoryCeiling = Math.Min(
                16384L * 1024 * 1024,
                (long)Math.Floor(memoryCapacity * 0.75));
            var cpuCeiling = Math.Min(8, cpuCapacity);
            var requestedMemory = ToBytes(configured.MemoryMiB) ?? defaults.MemoryBytes;
            if (configured.Cpu is not null && requestedCpu > cpuCeiling)
            {
                errors.Add($"Configured CPU limit {requestedCpu} exceeds the safe Docker ceiling {cpuCeiling}.");
            }

            if (configured.MemoryMiB is not null && requestedMemory > memoryCeiling)
            {
                errors.Add(
                    $"Configured memory limit {configured.MemoryMiB} MiB exceeds the safe Docker ceiling {memoryCeiling / 1024 / 1024} MiB.");
            }

            var effectiveMemory = configured.MemoryMiB is null
                ? Math.Min(defaults.MemoryBytes, memoryCeiling)
                : requestedMemory;
            var requestedTemp = ToBytes(configured.TempMiB) ?? defaults.TempBytes;
            var tempCeiling = effectiveMemory / 2;
            if (configured.TempMiB is not null && requestedTemp > tempCeiling)
            {
                errors.Add(
                    $"Configured /tmp limit {configured.TempMiB} MiB exceeds half the effective container memory.");
            }

            var effectiveTemp = configured.TempMiB is null
                ? Math.Min(requestedTemp, tempCeiling)
                : requestedTemp;
            var limits = new ContainerResourceLimits(
                configured.Cpu ?? Math.Min(defaults.Cpu, cpuCeiling),
                effectiveMemory,
                configured.Pids ?? defaults.Pids,
                effectiveTemp,
                ToBytes(configured.DockerLogMiB) ?? defaults.DockerLogBytes,
                configured.DockerLogFiles ?? defaults.DockerLogFiles,
                ToBytes(configured.StepLogMiB) ?? defaults.StepLogBytes);
            var evidence = new RunnerPreflightEvidence(
                errors.Count == 0 ? "passed" : "failed",
                engineVersion,
                operatingSystem,
                cgroupVersion,
                cgroupDriver,
                seccomp ? "verified" : "missing",
                rootless ? "verified" : "not-detected",
                userNamespace ? "verified" : "not-detected",
                desktop ? "verified" : "not-detected",
                "not-evaluated",
                localLoggingSupported ? "local" : "missing",
                "not-evaluated",
                cpuCapacity,
                memoryCapacity,
                cpuSupported,
                memorySupported,
                swapSupported,
                pidsSupported);
            return new DockerPreflightEvaluation(errors.Count == 0, limits, evidence, errors, warnings);
        }
        catch (JsonException ex)
        {
            return DockerPreflightEvaluation.Failed(
                $"Docker preflight returned invalid JSON: {ex.Message}");
        }
        catch (OverflowException)
        {
            return DockerPreflightEvaluation.Failed(
                "ACTIO_HOME/config.json contains a resource value that is too large.");
        }
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "not-reported"
            : "not-reported";

    private static double ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result
            : 0;

    private static long ReadLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static bool ReadBoolean(JsonElement root, string name, bool defaultValue)
        => root.TryGetProperty(name, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : defaultValue;

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : [];

    private static IReadOnlyList<string> ReadNestedStrings(
        JsonElement root,
        string parentName,
        string childName)
        => root.TryGetProperty(parentName, out var parent) &&
            parent.ValueKind == JsonValueKind.Object
            ? ReadStrings(parent, childName)
            : [];

    private static long? ToBytes(long? mebibytes)
        => mebibytes is null ? null : checked(mebibytes.Value * 1024 * 1024);
}

internal sealed record DockerPreflightEvaluation(
    bool Success,
    ContainerResourceLimits Limits,
    RunnerPreflightEvidence Evidence,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static DockerPreflightEvaluation Failed(string error)
        => new(false, ContainerResourceLimits.Defaults, new RunnerPreflightEvidence(Status: "failed"), [error], []);
}
