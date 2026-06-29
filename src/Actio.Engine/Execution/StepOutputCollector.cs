using Actio.Engine.Runs;

namespace Actio.Engine.Execution;

internal sealed class StepOutputCollector : IStepOutputSink, IAsyncDisposable
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly IStepLog _stepLog;
    private readonly OutputMarkerParser _outputMarkerParser;
    private readonly WorkflowCommandProcessor _workflowCommandProcessor;
    private readonly Dictionary<string, string> _capturedOutputs = new(StringComparer.Ordinal);
    private readonly List<StepLogAnnotation> _annotations = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StepOutputCollector(
        TextWriter output,
        TextWriter error,
        IStepLog stepLog,
        OutputMarkerParser outputMarkerParser,
        SecretMasker? secretMasker = null)
    {
        _output = output;
        _error = error;
        _stepLog = stepLog;
        _outputMarkerParser = outputMarkerParser;
        _workflowCommandProcessor = new WorkflowCommandProcessor(secretMasker);
    }

    public string? LogPath => _stepLog.LogPath;

    public IReadOnlyDictionary<string, string> CapturedOutputs => _capturedOutputs;

    public IReadOnlyList<StepLogAnnotation> Annotations => _annotations;

    public string Mask(string value)
    {
        return _workflowCommandProcessor.Mask(value);
    }

    public async Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            var result = _workflowCommandProcessor.Process(line);
            AddAnnotation(result);
            _output.WriteLine(result.Line);

            if (_outputMarkerParser.TryParse(result.Line, out var output))
            {
                _capturedOutputs[output.Key] = output.Value;
            }

            await _stepLog.WriteOutputLineAsync(result.Line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            var result = _workflowCommandProcessor.Process(line);
            AddAnnotation(result);
            _error.WriteLine(result.Line);
            await _stepLog.WriteErrorLineAsync(result.Line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return _stepLog.DisposeAsync();
    }

    private void AddAnnotation(WorkflowCommandProcessResult result)
    {
        if (result.Annotation is not null)
        {
            _annotations.Add(result.Annotation);
        }
    }
}
