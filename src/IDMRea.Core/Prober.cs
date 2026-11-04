using System.Net;
using System.Net.Http.Headers;
using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>Asks the server about a link before downloading: final URL, file name, size, and whether it can resume.</summary>
public static class Prober
{
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Use an absolute HTTP or HTTPS URL.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(Protocol.ProbeLimit);
        var response = await SendAsync(http, uri, ranged: true, deadline.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose(); response = await SendAsync(http, uri, ranged: false, deadline.Token);
        }
        using (response)
        {
            if (!TransferException.IsSuccess(response.StatusCode)) throw TransferException.FromResponse(response);
            var resolved = response.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;
            long? total; bool ranges;
            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { Unit: "bytes", Length: long length })
            { total = length; ranges = true; }
            else { total = response.StatusCode == HttpStatusCode.OK ? response.Content.Headers.ContentLength : null; ranges = false; }
            var etag = response.Headers.ETag; var modified = response.Content.Headers.LastModified;
            var type = response.Content.Headers.ContentType?.MediaType;
            var name = NameFor(response.Content.Headers.ContentDisposition?.ToString() ?? Header(response, "Content-Disposition"), new Uri(resolved), type);
            var resumable = ranges && total != null && ((etag != null && !etag.IsWeak) || modified != null);
            return new ProbeResult(uri.AbsoluteUri, resolved, name, total, etag?.ToString(), modified, type, Categories.From(name, type), resumable);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Content.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, bool ranged, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (ranged) request.Headers.Range = new RangeHeaderValue(0, 0);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static string NameFor(string? disposition, Uri resolved, string? contentType)
    {
        var name = FileNameFrom(disposition);
        if (string.IsNullOrWhiteSpace(name))
        {
            var segment = Uri.UnescapeDataString(resolved.AbsolutePath.Split('/').Last());
            name = Path.HasExtension(segment) ? segment : "download" + Categories.ExtensionFor(contentType);
        }
        // Keep only the last path component of whatever the server sent.
        name = name.Replace('\\', '/').Split('/').Last();
        return FileNames.SafeName(name);
    }

    /// <summary>The file name in a Content-Disposition header: RFC 5987 <c>filename*</c> wins over <c>filename</c>.</summary>
    public static string? FileNameFrom(string? contentDisposition)
    {
        if (string.IsNullOrWhiteSpace(contentDisposition) || !ContentDispositionHeaderValue.TryParse(contentDisposition, out var value)) return null;
        var name = !string.IsNullOrWhiteSpace(value.FileNameStar) ? value.FileNameStar : value.FileName?.Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
