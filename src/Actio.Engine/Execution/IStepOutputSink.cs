namespace Actio.Engine.Execution;

public interface IStepOutputSink
{
    Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default);

    Task WriteErrorLineAsync(string line, CancellationToken cancellationToken = default);
}
