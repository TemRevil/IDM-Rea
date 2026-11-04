namespace IDMRea.Contracts;

/// <summary>
/// What the browser sent with a download: some links only answer with the page's referrer and cookies. The cookies
/// belong to the host of <paramref name="CookiesFor"/> (the link the browser gave) and are sent to that host only.
/// <paramref name="Extra"/> holds the headers a video stream asks for (yt-dlp names them).
/// </summary>
public sealed record RequestHeaders(string? Referrer = null, string? Cookies = null, string? UserAgent = null, string? CookiesFor = null,
    IReadOnlyDictionary<string, string>? Extra = null)
{
    public bool IsEmpty => string.IsNullOrEmpty(Referrer) && string.IsNullOrEmpty(Cookies) && string.IsNullOrEmpty(UserAgent) && Extra is not { Count: > 0 };
}
