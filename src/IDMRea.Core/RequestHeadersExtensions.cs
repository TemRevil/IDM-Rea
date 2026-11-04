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
        if (!string.IsNullOrEmpty(headers.Cookies) && SameHost(headers.CookiesFor, request.RequestUri)) request.Headers.TryAddWithoutValidation("Cookie", headers.Cookies);
        if (!string.IsNullOrEmpty(headers.UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent", headers.UserAgent);
    }

    private static bool SameHost(string? cookiesFor, Uri? target) =>
        cookiesFor is null || (Uri.TryCreate(cookiesFor, UriKind.Absolute, out var origin) && target is not null
            && string.Equals(origin.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) && origin.Port == target.Port);
}
