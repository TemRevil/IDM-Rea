using System.Net;
using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>
/// Runs a video job: each stream is fetched in turn (plain files in capped parts, HLS lists segment by segment), then
/// the streams are joined into the job's part file. A stream address that stops working (403 or 410: signed links
/// expire) is asked of yt-dlp again once per run, and the download carries on from where it was.
/// </summary>
internal static class VideoTransfer
{
    public static async Task<DownloadJob> RunAsync(DownloadJob job, VideoServices services, TransferContext ctx, RequestHeaders? page, CancellationToken token)
    {
        var gate = new object();
        void Update(Func<VideoJob, VideoJob> change)
        {
            DownloadJob saved;
            lock (gate) { job = Totals(job with { Video = change(job.Video!) }); saved = job; }
            ctx.Checkpoint(saved);
        }

        var asked = false;
        for (var i = 0; i < job.Video!.Streams.Count; i++)
        {
            var index = i;
            if (job.Video.Streams[index].Done) continue;
            try { await StreamAsync(job.Video.Streams[index], s => Update(v => v with { Streams = Replace(v.Streams, index, s) }), ctx, page, token); }
            catch (Exception ex) when (!asked && Expired(ex) && !token.IsCancellationRequested)
            {
                asked = true;
                var fresh = await services.Streams.StreamsAsync(job.Video.PageUrl, job.Video.ChoiceId, page, token);
                Update(v => v with { Streams = Refreshed(v.Streams, fresh.Streams) });
                i--; // the same stream again, from its last checkpoint
            }
        }

        Update(v => v with { Stage = VideoStage.Joining });
        if (File.Exists(job.PartialPath)) File.Delete(job.PartialPath); // a join that was stopped starts over
        await services.Joiner.JoinAsync(job.Video.Streams.Select(s => s.PartialPath).ToArray(), job.PartialPath, job.Video.Container, token);
        var size = new FileInfo(job.PartialPath).Length;
        return job with { ReceivedBytes = size, TotalBytes = size, Parts = null };
    }

    /// <summary>Removes the streams' part files (after the join, or when the job is removed or cancelled).</summary>
    public static void DeleteStreamParts(DownloadJob job)
    {
        foreach (var stream in job.Video?.Streams ?? [])
            if (stream.PartialPath.Length > 0 && File.Exists(stream.PartialPath)) File.Delete(stream.PartialPath);
    }

    /// <summary>The job as it was before anything was downloaded.</summary>
    public static DownloadJob Reset(DownloadJob job) => Totals(job with
    {
        Video = job.Video! with { Stage = VideoStage.Downloading, Streams = job.Video.Streams.Select(Empty).ToArray() },
    });

    private static VideoStream Empty(VideoStream s) => s with { ReceivedBytes = 0, Parts = null, Done = false, SegmentsDone = 0 };

    private static async Task StreamAsync(VideoStream stream, Action<VideoStream> checkpoint, TransferContext ctx, RequestHeaders? page, CancellationToken token)
    {
        var headers = (page ?? new RequestHeaders()) with { Extra = stream.Headers };
        if (stream.Headers?.TryGetValue("User-Agent", out var agent) == true) headers = headers with { UserAgent = agent };
        var streamCtx = ctx with { Headers = headers, Checkpoint = _ => { } };
        if (stream.Protocol.StartsWith("m3u8", StringComparison.Ordinal))
        {
            var segments = await SegmentsAsync(new Uri(stream.Url), streamCtx, token);
            await SegmentTransfer.RunAsync(segments, stream.PartialPath, new SegmentProgress(stream.SegmentsDone, stream.ReceivedBytes), streamCtx,
                p => checkpoint(stream = stream with { SegmentsDone = p.Done, ReceivedBytes = p.Bytes }), token);
            checkpoint(stream with { Done = true, TotalBytes = stream.ReceivedBytes });
            return;
        }
        if (stream.Protocol is not ("https" or "http"))
            throw new VideoException("IDM-Rea can't download this kind of video stream.");

        var total = stream.TotalBytes;
        var resumable = true;
        if (total is not > 0)
        {
            var probe = await Prober.ProbeAsync(ctx.Http, stream.Url, token, headers);
            total = probe.TotalBytes; resumable = probe.Resumable;
            checkpoint(stream = stream with { TotalBytes = total });
        }
        var part = new DownloadJob(Guid.Empty, stream.Url, stream.PartialPath, stream.PartialPath, DownloadStatus.Downloading,
            stream.ReceivedBytes, total, Parts: stream.Parts, Resumable: resumable, ResolvedUrl: stream.Url);
        streamCtx = streamCtx with { Checkpoint = j => checkpoint(stream = stream with { ReceivedBytes = j.ReceivedBytes, Parts = j.Parts, TotalBytes = j.TotalBytes }) };
        part = resumable && total > 0
            ? await MultiPartTransfer.RunAsync(part, new ProbeResult(stream.Url, stream.Url, Path.GetFileName(stream.PartialPath), total, null, null, null,
                Category.Video, true, MaxRequestBytes: stream.ChunkSize, FixedFile: true), streamCtx, token)
            : await SingleStreamTransfer.RunAsync(part with { ReceivedBytes = 0, Parts = null }, streamCtx, token);
        checkpoint(stream with { ReceivedBytes = part.ReceivedBytes, Parts = part.Parts, TotalBytes = part.ReceivedBytes, Done = true });
    }

    /// <summary>The segments of an HLS address; a master list gives its best quality.</summary>
    private static async Task<IReadOnlyList<HlsSegment>> SegmentsAsync(Uri url, TransferContext ctx, CancellationToken token)
    {
        for (var depth = 0; ; depth++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ctx.Headers.ApplyTo(request);
            using var response = await ctx.Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(token);
            var at = response.RequestMessage?.RequestUri ?? url;
            if (!Hls.IsMaster(text)) return Hls.ParseMedia(text, at);
            if (depth > 0) throw new VideoException("The video's stream list points to itself.");
            url = Hls.ParseMaster(text, at).OrderByDescending(v => v.Bandwidth).FirstOrDefault()?.Url
                ?? throw new VideoException("IDM-Rea found no video on this page.");
        }
    }

    private static bool Expired(Exception ex) => ex switch
    {
        TransferException { HttpStatus: 403 or 410 } => true,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone } => true,
        _ => false,
    };

    /// <summary>Fresh addresses and headers for the same formats. A stream whose size changed starts over.</summary>
    private static IReadOnlyList<VideoStream> Refreshed(IReadOnlyList<VideoStream> saved, IReadOnlyList<VideoStream> fresh) => saved.Select(s =>
    {
        var now = fresh.FirstOrDefault(f => f.FormatId == s.FormatId)
            ?? throw new VideoException("The site no longer offers this quality. Remove the download and add it again.");
        if (s.Done) return s;
        var same = s.TotalBytes == null || now.TotalBytes == null || s.TotalBytes == now.TotalBytes;
        if (!same && File.Exists(s.PartialPath)) File.Delete(s.PartialPath);
        var updated = s with { Url = now.Url, Headers = now.Headers, Protocol = now.Protocol, ChunkSize = now.ChunkSize, TotalBytes = now.TotalBytes ?? s.TotalBytes };
        return same ? updated : Empty(updated);
    }).ToArray();

    private static IReadOnlyList<VideoStream> Replace(IReadOnlyList<VideoStream> streams, int index, VideoStream stream)
    {
        var copy = streams.ToArray(); copy[index] = stream; return copy;
    }

    /// <summary>The job's bytes are its streams' bytes; its size is known once every stream's size is.</summary>
    private static DownloadJob Totals(DownloadJob job)
    {
        var streams = job.Video!.Streams;
        return job with
        {
            ReceivedBytes = streams.Sum(s => s.ReceivedBytes),
            TotalBytes = streams.All(s => s.TotalBytes > 0) ? streams.Sum(s => s.TotalBytes!.Value) : null,
        };
    }
}
