using System.Net.Http.Headers;
using System.Security.Cryptography;
namespace IDMRea.Core;

/// <summary>How far a segment download got: segments appended to the file, and the file's length after them.</summary>
public sealed record SegmentProgress(int Done, long Bytes);

/// <summary>
/// Downloads an HLS stream's segments, several at once, and appends them to one file in play order. After each segment
/// the file is flushed and <c>checkpoint</c> is told, so a stopped download resumes after the last whole segment.
/// AES-128 segments are decrypted as they arrive; each key is fetched once.
/// </summary>
public static class SegmentTransfer
{
    public static async Task<SegmentProgress> RunAsync(IReadOnlyList<HlsSegment> segments, string output, SegmentProgress saved, TransferContext ctx,
        Action<SegmentProgress> checkpoint, CancellationToken token)
    {
        await using var file = new FileStream(output, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 0, true);
        var progress = saved.Done > 0 && saved.Done <= segments.Count && file.Length >= saved.Bytes ? saved : new SegmentProgress(0, 0);
        file.SetLength(progress.Bytes); file.Position = progress.Bytes; // drop a segment that was only partly written

        var keys = new Dictionary<Uri, Task<byte[]>>();
        Task<byte[]> KeyFor(Uri url) { lock (keys) return keys.TryGetValue(url, out var task) ? task : keys[url] = FetchAsync(ctx, url, null, null, token); }

        var ahead = Math.Clamp(ctx.Settings().PartsPerFile, 1, 16);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = new Queue<Task<byte[]>>();
        var next = progress.Done;
        void Fill() { while (queue.Count < ahead && next < segments.Count) queue.Enqueue(SegmentAsync(ctx, segments[next++], KeyFor, stop.Token)); }
        try
        {
            Fill();
            while (queue.Count > 0)
            {
                var data = await queue.Dequeue();
                Fill();
                ctx.BeforeWrite(file.Position);
                await file.WriteAsync(data, token);
                await file.FlushAsync(token); file.Flush(true);
                progress = new SegmentProgress(progress.Done + 1, file.Position);
                checkpoint(progress);
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(queue); } catch { /* the first failure is already on its way out */ }
        }
        return progress;
    }

    private static async Task<byte[]> SegmentAsync(TransferContext ctx, HlsSegment segment, Func<Uri, Task<byte[]>> keyFor, CancellationToken token)
    {
        var data = await FetchAsync(ctx, segment.Url, segment.Offset, segment.Length, token);
        if (segment.Key is null) return data;
        var key = await keyFor(segment.Key.Url);
        if (key.Length != 16) throw new IOException("The video's key is not a 16-byte AES key.");
        var iv = segment.Key.Iv ?? SequenceIv(segment.Sequence);
        using var aes = Aes.Create(); aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private static async Task<byte[]> FetchAsync(TransferContext ctx, Uri url, long? offset, long? length, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        ctx.Headers.ApplyTo(request);
        if (length is long l) request.Headers.Range = new RangeHeaderValue(offset ?? 0, (offset ?? 0) + l - 1);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await ctx.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(60));
            var read = await body.ReadAsync(buffer, idle.Token); if (read == 0) break;
            await ctx.Limiter.WaitAsync(read, token);
            memory.Write(buffer, 0, read);
            ctx.Meter.Add(read);
        }
        var data = memory.ToArray();
        // A server that ignores the range sends the whole file: keep only the asked-for piece.
        if (length is long want && response.StatusCode == System.Net.HttpStatusCode.OK && data.Length > want)
        {
            var from = (int)(offset ?? 0);
            if (from + want > data.Length) throw new IOException("A video segment ended early.");
            data = data.AsSpan(from, (int)want).ToArray();
        }
        else if (length is long exact && data.Length != exact) throw new IOException("A video segment ended early.");
        return data;
    }
}
