using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed class StepEnvironmentFileReader
{
    public async Task<StepEnvironmentFileResult> ReadAsync(
        StepEnvironmentFiles files,
        CancellationToken cancellationToken)
    {
        try
        {
            var environment = ParseKeyValueContent(
                await File.ReadAllTextAsync(files.EnvironmentFilePath, cancellationToken),
                StepEnvironmentFiles.EnvironmentFileName);
            var outputs = ParseKeyValueContent(
                await File.ReadAllTextAsync(files.OutputFilePath, cancellationToken),
                StepEnvironmentFiles.OutputFileName);
            var pathEntries = ParsePathContent(
                await File.ReadAllTextAsync(files.PathFilePath, cancellationToken));
            var summary = await File.ReadAllTextAsync(files.StepSummaryFilePath, cancellationToken);

            return new StepEnvironmentFileResult(
                environment.Values,
                outputs.Values,
                pathEntries,
                string.IsNullOrEmpty(summary) ? null : files.StepSummaryFilePath,
                string.IsNullOrEmpty(summary) ? null : summary,
                environment.Errors.Concat(outputs.Errors).ToArray());
        }
        catch (Exception ex) when (StorageError.IsRecoverable(ex))
        {
            return new StepEnvironmentFileResult(
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                [],
                null,
                null,
                [$"workflow environment files could not be read: {ex.Message}"]);
        }
    }

    private static KeyValueFileParseResult ParseKeyValueContent(string content, string fileName)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var lines = SplitLines(content);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var heredocIndex = line.IndexOf("<<", StringComparison.Ordinal);
            if (heredocIndex > 0)
            {
                var name = line[..heredocIndex];
                var delimiter = line[(heredocIndex + 2)..];
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(delimiter))
                {
                    errors.Add($"{fileName} line {index + 1} must use NAME<<DELIMITER syntax.");
                    continue;
                }

                var valueLines = new List<string>();
                var foundDelimiter = false;
                while (++index < lines.Length)
                {
                    if (string.Equals(lines[index], delimiter, StringComparison.Ordinal))
                    {
                        foundDelimiter = true;
                        break;
                    }

                    valueLines.Add(lines[index]);
                }

                if (!foundDelimiter)
                {
                    errors.Add($"{fileName} line {index + 1} is missing heredoc delimiter '{delimiter}'.");
                    break;
                }

                values[name] = string.Join("\n", valueLines);
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                errors.Add($"{fileName} line {index + 1} must use NAME=VALUE syntax.");
                continue;
            }

            values[line[..equalsIndex]] = line[(equalsIndex + 1)..];
        }

        return new KeyValueFileParseResult(values, errors);
    }

    private static IReadOnlyList<string> ParsePathContent(string content)
    {
        return SplitLines(content)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static string[] SplitLines(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private sealed record KeyValueFileParseResult(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyList<string> Errors);
}

internal sealed record StepEnvironmentFileResult(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<string> PathEntries,
    string? SummaryPath,
    string? Summary,
    IReadOnlyList<string> Errors);
