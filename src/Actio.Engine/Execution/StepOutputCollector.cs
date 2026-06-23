using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed class StepOutputCollector : IStepOutputSink, IAsyncDisposable
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly IStepLog _stepLog;
    private readonly OutputMarkerParser _outputMarkerParser;
    private readonly Dictionary<string, string> _capturedOutputs = new(StringComparer.Ordinal);

    public StepOutputCollector(
        TextWriter output,
        TextWriter error,
        IStepLog stepLog,
        OutputMarkerParser outputMarkerParser)
    {
        _output = output;
        _error = error;
        _stepLog = stepLog;
        _outputMarkerParser = outputMarkerParser;
    }

    public string? LogPath => _stepLog.LogPath;

    public IReadOnlyDictionary<string, string> CapturedOutputs => _capturedOutputs;

    public async Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        _output.WriteLine(line);

        if (_outputMarkerParser.TryParse(line, out var output))
        {
            _capturedOutputs[output.Key] = output.Value;
        }

        await _stepLog.WriteOutputLineAsync(line, cancellationToken);
    }

    public async Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default)
    {
        _error.WriteLine(line);
        await _stepLog.WriteErrorLineAsync(line, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _stepLog.DisposeAsync();
    }
}
