namespace IDMRea.Contracts;

/// <summary>
/// What the browser sent with a download: some links only answer with the page's referrer and cookies. The cookies
/// belong to the host of <paramref name="CookiesFor"/> (the link the browser gave) and are sent to that host only.
/// </summary>
public sealed record RequestHeaders(string? Referrer = null, string? Cookies = null, string? UserAgent = null, string? CookiesFor = null)
{
    public bool IsEmpty => string.IsNullOrEmpty(Referrer) && string.IsNullOrEmpty(Cookies) && string.IsNullOrEmpty(UserAgent);
}
