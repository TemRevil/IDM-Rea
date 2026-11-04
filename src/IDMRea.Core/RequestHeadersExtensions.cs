using IDMRea.Contracts;
namespace IDMRea.Core;

public static class RequestHeadersExtensions
{
    /// <summary>Adds the browser's referrer, cookies and user agent to a request; empty values are left out.</summary>
    public static void ApplyTo(this RequestHeaders? headers, HttpRequestMessage request)
    {
        if (headers is null) return;
        if (Uri.TryCreate(headers.Referrer, UriKind.Absolute, out var referrer) && referrer.Scheme is "http" or "https") request.Headers.Referrer = referrer;
        if (!string.IsNullOrEmpty(headers.Cookies)) request.Headers.TryAddWithoutValidation("Cookie", headers.Cookies);
        if (!string.IsNullOrEmpty(headers.UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent", headers.UserAgent);
    }
}
