namespace Actio.Web;

public sealed record ActioWebOptions(
    string ProjectRoot,
    string ActioHome,
    string Url = ActioWebDefaults.DefaultUrl,
    bool Background = false,
    string? RuntimeIdentity = null,
    string? WebInstanceId = null,
    int? ProcessId = null,
    long? ProcessStartTimeUtcTicks = null,
    string? ControlToken = null,
    string? SessionId = null);
