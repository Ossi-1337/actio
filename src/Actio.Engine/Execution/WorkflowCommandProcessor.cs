using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed class WorkflowCommandProcessor
{
    private readonly SecretMasker _masker;
    private string? _stoppedToken;

    public WorkflowCommandProcessor(SecretMasker? masker = null)
    {
        _masker = masker ?? new SecretMasker();
    }

    public string Mask(string value)
    {
        return _masker.Mask(value);
    }

    public WorkflowCommandProcessResult Process(string line)
    {
        if (_stoppedToken is not null)
        {
            if (string.Equals(line, $"::{_stoppedToken}::", StringComparison.Ordinal))
            {
                _stoppedToken = null;
                return new WorkflowCommandProcessResult("[command] workflow commands resumed", null);
            }

            return new WorkflowCommandProcessResult(_masker.Mask(line), null);
        }

        if (!WorkflowCommand.TryParse(line, out var command))
        {
            return new WorkflowCommandProcessResult(_masker.Mask(line), null);
        }

        return command.Name switch
        {
            "debug" or "notice" or "warning" or "error" => CreateAnnotationResult(command),
            "group" => new WorkflowCommandProcessResult($"[group] {_masker.Mask(command.Message)}", null),
            "endgroup" => new WorkflowCommandProcessResult("[endgroup]", null),
            "add-mask" => RegisterMask(command.Message),
            "stop-commands" => StopCommands(command.Message),
            _ => new WorkflowCommandProcessResult(_masker.Mask(line), null)
        };
    }

    private WorkflowCommandProcessResult CreateAnnotationResult(WorkflowCommand command)
    {
        var message = _masker.Mask(command.Message);
        return new WorkflowCommandProcessResult(
            $"[{command.Name}] {message}",
            new StepLogAnnotation(
                command.Name,
                message,
                GetMaskedProperty(command, "title"),
                GetMaskedProperty(command, "file"),
                GetIntProperty(command, "line"),
                GetIntProperty(command, "endLine"),
                GetIntProperty(command, "col"),
                GetIntProperty(command, "endColumn")));
    }

    private WorkflowCommandProcessResult RegisterMask(string value)
    {
        _masker.Add(value);
        return new WorkflowCommandProcessResult("[command] add-mask registered", null);
    }

    private WorkflowCommandProcessResult StopCommands(string token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _stoppedToken = token;
        }

        return new WorkflowCommandProcessResult("[command] workflow commands stopped", null);
    }

    private static string? GetProperty(WorkflowCommand command, string name)
        => command.Properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private string? GetMaskedProperty(WorkflowCommand command, string name)
    {
        var value = GetProperty(command, name);
        return value is null ? null : _masker.Mask(value);
    }

    private static int? GetIntProperty(WorkflowCommand command, string name)
        => int.TryParse(GetProperty(command, name), out var value) ? value : null;
}

internal sealed class SecretMasker
{
    private const string MaskReplacement = "***";
    private readonly Lock _lock = new();
    private readonly List<string> _masks = [];

    public void Add(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_lock)
        {
            if (!_masks.Contains(value, StringComparer.Ordinal))
            {
                _masks.Add(value);
            }
        }
    }

    public string Mask(string value)
    {
        string[] masks;
        lock (_lock)
        {
            masks = _masks
                .OrderByDescending(item => item.Length)
                .ToArray();
        }

        var masked = value;
        foreach (var mask in masks)
        {
            masked = masked.Replace(mask, MaskReplacement, StringComparison.Ordinal);
        }

        return masked;
    }
}

internal sealed record WorkflowCommandProcessResult(
    string Line,
    StepLogAnnotation? Annotation);

internal sealed record WorkflowCommand(
    string Name,
    IReadOnlyDictionary<string, string> Properties,
    string Message)
{
    public static bool TryParse(string line, out WorkflowCommand command)
    {
        if (!line.StartsWith("::", StringComparison.Ordinal))
        {
            command = default!;
            return false;
        }

        var separatorIndex = line.IndexOf("::", 2, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            command = default!;
            return false;
        }

        var header = line[2..separatorIndex];
        var spaceIndex = header.IndexOf(' ', StringComparison.Ordinal);
        var name = (spaceIndex < 0 ? header : header[..spaceIndex]).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
        {
            command = default!;
            return false;
        }

        var properties = spaceIndex < 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseProperties(header[(spaceIndex + 1)..]);
        command = new WorkflowCommand(name, properties, Unescape(line[(separatorIndex + 2)..]));
        return true;
    }

    private static Dictionary<string, string> ParseProperties(string value)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = segment.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex <= 0)
            {
                continue;
            }

            properties[segment[..equalsIndex]] = Unescape(segment[(equalsIndex + 1)..]);
        }

        return properties;
    }

    private static string Unescape(string value)
    {
        return value
            .Replace("%0D", "\r", StringComparison.OrdinalIgnoreCase)
            .Replace("%0A", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("%3A", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("%2C", ",", StringComparison.OrdinalIgnoreCase)
            .Replace("%25", "%", StringComparison.OrdinalIgnoreCase);
    }
}
