using IDMRea.Contracts;
namespace IDMRea.Core;

public static class RequestHeadersExtensions
{
    /// <summary>
    /// Adds the browser's referrer, cookies and user agent to a request; empty values are left out, and the cookies are
    /// left out of a request to any host other than the one they came for (a saved link after a redirect, say).
    /// </summary>
    public static void ApplyTo(this RequestHeaders? headers, HttpRequestMessage request)
    {
        if (headers is null) return;
        if (Uri.TryCreate(headers.Referrer, UriKind.Absolute, out var referrer) && referrer.Scheme is "http" or "https") request.Headers.Referrer = referrer;
        if (!string.IsNullOrEmpty(headers.Cookies) && headers.CookiesGoTo(request.RequestUri)) request.Headers.TryAddWithoutValidation("Cookie", headers.Cookies);
        if (!string.IsNullOrEmpty(headers.UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent", headers.UserAgent);
        foreach (var (name, value) in headers.Extra ?? new Dictionary<string, string>())
            if (!Reserved.Contains(name) && !request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>Headers the engine sets itself, or that would send something to the wrong host.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Cookie", "Range", "If-Range", "Accept-Encoding", "Content-Length", "Connection", "Transfer-Encoding", "User-Agent", "Referer" };

    /// <summary>The browser's cookies go only to the host (and port) they were read for.</summary>
    public static bool CookiesGoTo(this RequestHeaders headers, Uri? target) =>
        headers.CookiesFor is not { } cookiesFor || (Uri.TryCreate(cookiesFor, UriKind.Absolute, out var origin) && target is not null
            && string.Equals(origin.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) && origin.Port == target.Port);
}
