using Actio.Engine.Runs;
using System.Text;

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
    private readonly long _maxLogBytes;
    private long _storedLogBytes;
    private bool _logTruncated;
    private const string TruncationMarker = "[actio] step log truncated because the configured size limit was reached.";

    public StepOutputCollector(
        TextWriter output,
        TextWriter error,
        IStepLog stepLog,
        OutputMarkerParser outputMarkerParser,
        SecretMasker? secretMasker = null,
        long maxLogBytes = long.MaxValue)
    {
        _output = output;
        _error = error;
        _stepLog = stepLog;
        _outputMarkerParser = outputMarkerParser;
        _workflowCommandProcessor = new WorkflowCommandProcessor(secretMasker);
        _maxLogBytes = maxLogBytes;
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

            await WriteLogLineAsync(result.Line, isError: false, cancellationToken);
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
            await WriteLogLineAsync(result.Line, isError: true, cancellationToken);
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

    private async Task WriteLogLineAsync(
        string line,
        bool isError,
        CancellationToken cancellationToken)
    {
        if (_logTruncated)
        {
            return;
        }

        var prefix = isError ? "[stderr] " : "[stdout] ";
        var byteCount = Encoding.UTF8.GetByteCount(prefix + line + Environment.NewLine);
        if (_storedLogBytes + byteCount <= _maxLogBytes)
        {
            _storedLogBytes += byteCount;
            if (isError)
            {
                await _stepLog.WriteErrorLineAsync(line, cancellationToken);
            }
            else
            {
                await _stepLog.WriteOutputLineAsync(line, cancellationToken);
            }

            return;
        }

        _logTruncated = true;
        await _stepLog.WriteErrorLineAsync(TruncationMarker, cancellationToken);
    }
}
