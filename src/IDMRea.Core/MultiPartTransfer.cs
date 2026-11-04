using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using IDMRea.Contracts;
using Microsoft.Win32.SafeHandles;
namespace IDMRea.Core;

/// <summary>
/// Several ranged connections writing into one preallocated file. A part that finishes early takes half of the
/// largest remaining part, so the slowest connection never holds the download up alone.
/// </summary>
public static class MultiPartTransfer
{
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250), CheckpointEvery = TimeSpan.FromMilliseconds(500);
    private const int Reconnects = 3;

    private sealed class RangesRefused() : Exception("The server ignored the range of a part.");
    private enum Outcome { Done, Busy }

    /// <summary>Returns the job with every part complete and <c>ReceivedBytes == TotalBytes</c>, before hashing.</summary>
    public static async Task<DownloadJob> RunAsync(DownloadJob job, ProbeResult probe, TransferContext ctx, CancellationToken token)
    {
        var total = probe.TotalBytes is > 0 and long t ? t : throw new ArgumentException("A multi-part download needs a known size.");
        var run = new Run(job, probe, total, ctx);
        Exception? failure = null;
        using (var handle = File.OpenHandle(job.PartialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
        {
            run.Prepare(handle);
            try { await run.CoordinateAsync(token); }
            catch (Exception ex) { failure = ex; }
            if (failure is not RangesRefused) run.Checkpoint();
        }
        if (failure is RangesRefused)
            return await SingleStreamTransfer.RunAsync(run.Job with { Parts = null, ReceivedBytes = 0, Resumable = false }, ctx, token);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return run.Job;
    }

    private sealed class Run(DownloadJob job, ProbeResult probe, long total, TransferContext ctx)
    {
        private readonly object gate = new();
        private readonly List<DownloadPart> parts = [];   // Received = bytes written
        private readonly List<long> claimed = [];         // bytes read and assigned to a write, may run ahead of Received
        private readonly Queue<int> pending = new();
        private readonly Dictionary<int, Task<(Outcome, TransferException?)>> active = [];
        private readonly bool strong = SingleStreamTransfer.Strong(probe.ETag);
        private SafeFileHandle handle = null!;
        private int busyCap = int.MaxValue;
        public DownloadJob Job { get; private set; } = job;

        public void Prepare(SafeFileHandle file)
        {
            handle = file;
            var settings = ctx.Settings();
            var saved = Job.Parts;
            var keep = saved is { Count: > 0 } && saved.Sum(p => p.Length) == total && RandomAccess.GetLength(handle) == total;
            parts.AddRange(keep ? saved! : PartPlanner.Initial(total, settings.EffectiveParts, settings.MinPartSize));
            if (RandomAccess.GetLength(handle) != total) RandomAccess.SetLength(handle, total);
            claimed.AddRange(parts.Select(p => p.Received));
            for (var i = 0; i < parts.Count; i++) if (!parts[i].Done) pending.Enqueue(i);
            Job = Job with { TotalBytes = total, ETag = probe.ETag, LastModified = probe.LastModified, ResolvedUrl = probe.ResolvedUrl, Resumable = true, CompletionSha256 = null };
            Checkpoint();
        }

        public void Checkpoint()
        {
            DownloadJob snapshot;
            lock (gate) snapshot = Job with { Parts = parts.ToArray(), ReceivedBytes = parts.Sum(p => p.Received) };
            // Only bytes counted in the snapshot are promised, and they were written before it was taken.
            RandomAccess.FlushToDisk(handle);
            Job = snapshot; ctx.Checkpoint(snapshot);
        }

        public async Task CoordinateAsync(CancellationToken token)
        {
            using var stopAll = CancellationTokenSource.CreateLinkedTokenSource(token);
            var started = Stopwatch.StartNew(); var saved = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    var settings = ctx.Settings();
                    var cap = Math.Min(settings.EffectiveParts, busyCap);
                    var ramp = settings.UltraPerformance ? cap : (int)Math.Min(cap, 1 + started.Elapsed.Ticks / Stagger.Ticks);
                    while (active.Count < ramp && (pending.TryDequeue(out var next) ? next : Split()) is int index)
                        active[index] = PartAsync(index, stopAll.Token);
                    if (active.Count == 0) break;

                    var wait = Task.Delay(Stagger, stopAll.Token);
                    await Task.WhenAny(active.Values.Append(wait));
                    token.ThrowIfCancellationRequested();
                    foreach (var (index, task) in active.Where(a => a.Value.IsCompleted).ToArray())
                    {
                        active.Remove(index);
                        var (outcome, busy) = await task; // rethrows a part's error as the job's error
                        if (outcome != Outcome.Busy) continue;
                        if (active.Count == 0) throw busy!;
                        pending.Enqueue(index); busyCap = active.Count;
                    }
                    if (saved.Elapsed >= CheckpointEvery) { Checkpoint(); saved.Restart(); }
                }
                lock (gate) if (parts.Any(p => !p.Done)) throw new IOException("A part ended before it was complete.");
            }
            finally
            {
                stopAll.Cancel();
                try { await Task.WhenAll(active.Values); } catch { /* the first error is already on its way up */ }
            }
        }

        /// <summary>Halves the running part with the most left; the second half becomes a new part.</summary>
        private int? Split()
        {
            lock (gate)
            {
                var view = parts.Select((p, i) => p with { Received = claimed[i] }).ToArray();
                if (PartPlanner.PickToSplit(view, active.Keys.ToHashSet()) is not int i) return null;
                var from = parts[i].Start + claimed[i]; var left = parts[i].End - from + 1;
                var newEnd = from + (left - left / 2) - 1;
                parts.Add(new DownloadPart(newEnd + 1, parts[i].End, 0)); claimed.Add(0);
                parts[i] = parts[i] with { End = newEnd };
                return parts.Count - 1;
            }
        }

        private static bool Dropped(Exception ex) =>
            ex is not TransferException and not RangesRefused && RetryPolicy.Classify(ex) is ErrorKind.Network or ErrorKind.Timeout;

        private bool SameEntity(HttpResponseMessage response) => strong
            ? response.Headers.ETag is { IsWeak: false } tag && tag.ToString() == probe.ETag
            : probe.LastModified != null && response.Content.Headers.LastModified == probe.LastModified;

        private async Task<(Outcome, TransferException?)> PartAsync(int index, CancellationToken token)
        {
            await Task.Yield();
            var buffer = ArrayPool<byte>.Shared.Rent(ctx.Settings().BufferSize);
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { return await ConnectAsync(index, buffer, token); }
                    catch (Exception ex) when (attempt < Reconnects && !token.IsCancellationRequested && Dropped(ex))
                    { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private async Task<(Outcome, TransferException?)> ConnectAsync(int index, byte[] buffer, CancellationToken token)
        {
            long from, to;
            lock (gate) { claimed[index] = parts[index].Received; from = parts[index].Start + parts[index].Received; to = parts[index].End; }
            if (from > to) return (Outcome.Done, null);
            using var request = new HttpRequestMessage(HttpMethod.Get, probe.ResolvedUrl);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.Range = new RangeHeaderValue(from, to);
            request.Headers.IfRange = strong ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(probe.ETag!)) : new RangeConditionHeaderValue(probe.LastModified!.Value);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await ctx.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) return (Outcome.Busy, TransferException.FromResponse(response));
            if (!TransferException.IsSuccess(response.StatusCode)) throw TransferException.FromResponse(response);
            if (!SameEntity(response)) throw new TransferException(ErrorKind.Changed, "The file changed on the server.");
            if (response.StatusCode != HttpStatusCode.PartialContent) throw new RangesRefused();
            if (response.Content.Headers.ContentRange is not { Unit: "bytes", From: long f, To: long l, Length: long size } || f != from || l != to || size != total)
                throw new TransferException(ErrorKind.Other, "The server sent the wrong part of the file.");
            if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new TransferException(ErrorKind.Other, "Unsupported content encoding.");
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await body.ReadAsync(buffer, idle.Token);
                long at; int take; bool shrunk;
                lock (gate)
                {
                    if (read == 0) { if (parts[index].Done) return (Outcome.Done, null); break; }
                    var part = parts[index]; shrunk = part.End < to;
                    at = part.Start + claimed[index]; take = (int)Math.Min(read, part.End - at + 1); claimed[index] += take;
                }
                if (take < read && !shrunk) throw new TransferException(ErrorKind.Other, "The server sent more bytes than the part it was asked for.");
                if (take > 0)
                {
                    await ctx.Limiter.WaitAsync(take, token);
                    ctx.BeforeWrite(at);
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, take), at, token);
                    lock (gate) parts[index] = parts[index] with { Received = parts[index].Received + take };
                    ctx.Meter.Add(take);
                }
                // A part cut short by a split ends here; a whole part reads on so an overrun is caught.
                if (shrunk) lock (gate) if (parts[index].Done) return (Outcome.Done, null);
            }
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The connection closed before the part was complete.");
        }
    }
}
