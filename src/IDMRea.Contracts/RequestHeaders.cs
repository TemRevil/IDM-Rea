namespace IDMRea.Contracts;

/// <summary>What the browser sent with a download: some links only answer with the page's referrer and cookies.</summary>
public sealed record RequestHeaders(string? Referrer = null, string? Cookies = null, string? UserAgent = null)
{
    public bool IsEmpty => string.IsNullOrEmpty(Referrer) && string.IsNullOrEmpty(Cookies) && string.IsNullOrEmpty(UserAgent);
}
