using System.Net;
namespace IDMRea.Core;

/// <summary>
/// Follows redirects itself (the inner handler must not), so a browser download's cookies stay with the host they came
/// from: once a redirect leaves that host, the Cookie and Authorization headers are dropped. Like the built-in
/// handler, it follows at most <see cref="MaxRedirects"/> hops and never goes from HTTPS to HTTP.
/// </summary>
public sealed class RedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var origin = request.RequestUri!;
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, token);
            if (hop == MaxRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location) return response;
            var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (next.Scheme is not ("http" or "https") || (current.RequestUri!.Scheme == "https" && next.Scheme == "http")) return response;
            response.Dispose();
            var method = response.StatusCode == HttpStatusCode.SeeOther && current.Method != HttpMethod.Head ? HttpMethod.Get : current.Method;
            var follow = new HttpRequestMessage(method, next) { Version = current.Version, VersionPolicy = current.VersionPolicy };
            var sameHost = string.Equals(next.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) && next.Port == origin.Port;
            foreach (var header in current.Headers)
            {
                if (!sameHost && (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) continue;
                follow.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (current != request) current.Dispose();
            current = follow;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
