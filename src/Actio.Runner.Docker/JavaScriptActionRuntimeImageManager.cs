using Actio.Engine.Execution;
using System.Collections.Concurrent;

namespace Actio.Runner.Docker;

internal sealed class JavaScriptActionRuntimeImageManager
{
    private readonly IJavaScriptActionRuntimeImageStore _store;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runtimeLocks = new(StringComparer.Ordinal);

    public JavaScriptActionRuntimeImageManager()
        : this(new DockerJavaScriptActionRuntimeImageStore())
    {
    }

    internal JavaScriptActionRuntimeImageManager(IJavaScriptActionRuntimeImageStore store)
    {
        _store = store;
    }

    public async Task<JavaScriptActionRuntimeImageResult> EnsureAsync(
        JavaScriptActionRuntimeDescriptor runtime,
        IStepOutputSink output,
        CancellationToken cancellationToken)
    {
        var runtimeLock = _runtimeLocks.GetOrAdd(runtime.Runtime, _ => new SemaphoreSlim(1, 1));
        await runtimeLock.WaitAsync(cancellationToken);
        try
        {
            var inspection = await _store.InspectAsync(runtime.Image, cancellationToken);
            if (inspection.Matches(runtime.ExpectedLabels))
            {
                return JavaScriptActionRuntimeImageResult.Ready(runtime.Image);
            }

            await output.WriteOutputLineAsync(
                $"Preparing Actio JavaScript runtime '{runtime.Runtime}' from pinned base '{runtime.BaseImage}'.",
                cancellationToken);
            var build = await _store.BuildAsync(
                runtime,
                JavaScriptActionRuntimeDockerfile.Content,
                output,
                cancellationToken);
            if (!build.Success)
            {
                return JavaScriptActionRuntimeImageResult.Failed(
                    $"Actio could not prepare JavaScript runtime '{runtime.Runtime}' from pinned base " +
                    $"'{runtime.BaseImage}'. {build.Error}");
            }

            var builtInspection = await _store.InspectAsync(runtime.Image, cancellationToken);
            if (!builtInspection.Matches(runtime.ExpectedLabels))
            {
                return JavaScriptActionRuntimeImageResult.Failed(
                    $"Actio built JavaScript runtime '{runtime.Runtime}', but image '{runtime.Image}' " +
                    "does not contain the expected provenance labels.");
            }

            return JavaScriptActionRuntimeImageResult.Ready(runtime.Image);
        }
        finally
        {
            runtimeLock.Release();
        }
    }
}

internal sealed record JavaScriptActionRuntimeImageResult(
    bool Success,
    string? Image,
    string? Error)
{
    public static JavaScriptActionRuntimeImageResult Ready(string image) => new(true, image, null);

    public static JavaScriptActionRuntimeImageResult Failed(string error) => new(false, null, error);
}

internal sealed record JavaScriptActionRuntimeImageInspection(
    bool Exists,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    public IReadOnlyDictionary<string, string> Labels { get; init; } = Labels ??
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Matches(IReadOnlyDictionary<string, string> expected)
    {
        return Exists && expected.All(item =>
            Labels.TryGetValue(item.Key, out var value) &&
            string.Equals(value, item.Value, StringComparison.Ordinal));
    }
}

internal sealed record JavaScriptActionRuntimeImageBuildResult(
    bool Success,
    string? Error = null);

internal interface IJavaScriptActionRuntimeImageStore
{
    Task<JavaScriptActionRuntimeImageInspection> InspectAsync(
        string image,
        CancellationToken cancellationToken);

    Task<JavaScriptActionRuntimeImageBuildResult> BuildAsync(
        JavaScriptActionRuntimeDescriptor runtime,
        string dockerfile,
        IStepOutputSink output,
        CancellationToken cancellationToken);
}
