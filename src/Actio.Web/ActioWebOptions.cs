namespace Actio.Web;

public sealed record ActioWebOptions(
    string ProjectRoot,
    string ActioHome,
    string Url = ActioWebDefaults.DefaultUrl);
