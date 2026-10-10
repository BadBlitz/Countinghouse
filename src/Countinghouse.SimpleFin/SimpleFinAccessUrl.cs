using System.Text;

namespace Countinghouse.SimpleFin;

// A claimed Access URL carries its credentials inline: https://user:pass@bridge.simplefin.org/simplefin
//
// HttpClient deliberately ignores that userinfo section — it won't build an Authorization header
// from it — so the credentials have to be split out and sent manually. Requesting the URL as-is
// returns 403 with no hint as to why, which is a slow thing to debug.
public sealed record SimpleFinAccessUrl(string BaseUrl, string UserName, string Password)
{
    public string AuthorizationParameter =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}"));

    public static bool TryParse(string? accessUrl, out SimpleFinAccessUrl parsed, out string? error)
    {
        parsed = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(accessUrl))
        {
            error = "The access URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(accessUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "The access URL isn't a valid http(s) URL.";
            return false;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2 || userInfo[0].Length == 0)
        {
            error = "The access URL has no embedded credentials — it may have been truncated when copied.";
            return false;
        }

        // Rebuilt without the userinfo so the credentials can't leak into a log or an exception
        // message that includes the request URI.
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        var baseUrl = builder.Uri.ToString().TrimEnd('/');

        parsed = new SimpleFinAccessUrl(
            baseUrl,
            Uri.UnescapeDataString(userInfo[0]),
            Uri.UnescapeDataString(userInfo[1]));
        return true;
    }
}
