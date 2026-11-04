using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>One connection from the saved offset to the end. Used when the server cannot serve parts.</summary>
public static class SingleStreamTransfer
{
    internal static bool Strong(string? tag) => EntityTagHeaderValue.TryParse(tag, out var parsed) && !parsed.IsWeak;

    /// <summary>Returns the job with every byte on disk and flushed, before hashing.</summary>
    public static async Task<DownloadJob> RunAsync(DownloadJob job, TransferContext ctx, CancellationToken token)
    {
        var size = File.Exists(job.PartialPath) ? new FileInfo(job.PartialPath).Length : 0;
        var offset = Math.Min(size, job.ReceivedBytes);
        if ((!Strong(job.ETag) && job.LastModified == null) || job.TotalBytes is long knownSize && offset >= knownSize) offset = 0;
        using var file = new FileStream(job.PartialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 0, true);
        file.SetLength(offset); file.Position = offset;
        using var request = new HttpRequestMessage(HttpMethod.Get, job.ResolvedUrl ?? job.Url);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        ctx.Headers.ApplyTo(request);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = Strong(job.ETag) ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(job.ETag!)) : new RangeConditionHeaderValue(job.LastModified!.Value);
        }
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await ctx.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new IOException("Unsupported content encoding.");
        var etag = response.Headers.ETag?.ToString(); var modified = response.Content.Headers.LastModified;
        long? total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range == null || range.Unit != "bytes" || range.From != offset || range.To == null || range.Length == null || range.To != range.Length - 1 || range.To < range.From || (response.Content.Headers.ContentLength is long length && length != range.To - range.From + 1)) throw new IOException("Invalid Content-Range.");
            if (offset > 0 && ((Strong(job.ETag) && etag != job.ETag) || (!Strong(job.ETag) && modified != job.LastModified) || (job.TotalBytes != null && job.TotalBytes != range.Length))) throw new IOException("The resumed entity changed.");
            total = range.Length;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        { offset = 0; file.SetLength(0); file.Position = 0; total = response.Content.Headers.ContentLength; }
        else throw new IOException("Expected a complete or ranged HTTP response.");
        job = job with { ReceivedBytes = offset, TotalBytes = total, ETag = etag, LastModified = modified, CompletionFileId = null }; ctx.Checkpoint(job);
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = ArrayPool<byte>.Shared.Rent(ctx.Settings().BufferSize); var checkpoint = Environment.TickCount64;
        try
        {
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await body.ReadAsync(buffer, idle.Token); if (read == 0) break;
                if (job.TotalBytes is long declared && read > declared - job.ReceivedBytes)
                    throw new IOException("The server sent more bytes than the declared representation.");
                await ctx.Limiter.WaitAsync(read, token);
                ctx.BeforeWrite(file.Position);
                await file.WriteAsync(buffer.AsMemory(0, read), token); job = job with { ReceivedBytes = job.ReceivedBytes + read };
                ctx.Meter.Add(read);
                if (Environment.TickCount64 - checkpoint > 500) { file.Flush(true); ctx.Checkpoint(job); checkpoint = Environment.TickCount64; }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); file.Flush(true); ctx.Checkpoint(job); }
        if (job.TotalBytes is long expected && expected != job.ReceivedBytes) throw new IOException("The response ended before the expected size.");
        return job;
    }
}
