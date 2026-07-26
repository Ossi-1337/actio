using System.Net;

namespace Actio.Web;

public static class LoopbackWebUrlPolicy
{
    public static bool TryValidate(
        string value,
        bool allowDynamicPort,
        out Uri? url,
        out string? error)
    {
        url = null;
        error = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = "Web URL must be an absolute HTTP URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(candidate.UserInfo) ||
            candidate.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            error = "Web URL cannot contain user information, a path, query, or fragment.";
            return false;
        }

        var host = candidate.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
        {
            error = "Web URL host must be a literal loopback address in 127.0.0.0/8 or [::1].";
            return false;
        }

        if (candidate.Port == 0 && !allowDynamicPort)
        {
            error = "Web URL port 0 is reserved for managed background workers.";
            return false;
        }

        if (candidate.Port is < 0 or > 65535)
        {
            error = "Web URL port must be between 1 and 65535.";
            return false;
        }

        url = candidate;
        return true;
    }

    public static Uri Validate(string value, bool allowDynamicPort)
    {
        if (TryValidate(value, allowDynamicPort, out var url, out var error))
        {
            return url!;
        }

        throw new ArgumentException(error, nameof(value));
    }
}
