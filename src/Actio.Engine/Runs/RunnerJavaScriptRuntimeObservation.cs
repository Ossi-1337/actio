namespace Actio.Engine.Runs;

public sealed record RunnerJavaScriptRuntimeObservation(
    string Surface,
    string Runtime,
    string Image,
    string? BaseImage = null,
    string? DefinitionHash = null,
    string? NodeVersion = null,
    string? GitVersion = null,
    string? CaCertificatesVersion = null);
