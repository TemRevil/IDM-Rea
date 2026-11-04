using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
namespace IDMRea.Tests;

/// <summary>A small HTTP/1.1 server on loopback with switchable misbehaviour, for download tests.</summary>
internal sealed class Fixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> requests = [];
    private readonly Lazy<byte[]> body;
    public Fixture() { body = new(() => RandomNumberGenerator.GetBytes(BodySize)); listener.Start(); loop = Accept(); }

    public int BodySize { get; init; } = 1024 * 1024;
    public byte[] Body => body.Value;
    public string Mode { get; init; } = "range";
    public int Delay { get; init; }
    public string Path { get; init; } = "/file.bin";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RedirectOnce { get; init; }
    public int? Status { get; init; }
    /// <summary>The first request number (from 1) that gets <see cref="Status"/>; earlier ones are served normally.</summary>
    public int StatusFrom { get; init; } = 1;
    /// <summary>From this request number on, the connection is closed without any response.</summary>
    public int? CloseFrom { get; init; }
    /// <summary>Apply <see cref="Delay"/> only to requests whose range starts at byte 0.</summary>
    public bool SlowFirstPart { get; init; }
    public int ChunkSize { get; init; } = 8192;
    public string? LastRange { get; private set; }
    public ConcurrentQueue<string?> Ranges { get; } = new();
    public ConcurrentQueue<string> Targets { get; } = new();
    private int requestCount, redirected, current, maxConcurrent, dropped;
    public int RequestCount => Volatile.Read(ref requestCount);
    public int MaxConcurrent => Volatile.Read(ref maxConcurrent);
    public bool Dropped => Volatile.Read(ref dropped) == 1;
    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{Path}";

    private async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); requests.Add(Serve(client)); } }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var requestLine = await reader.ReadLineAsync(stop.Token) ?? ""; string? line; string? range = null;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token))) if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
            var index = Interlocked.Increment(ref requestCount);
            var now = Interlocked.Increment(ref current); int seen;
            while (now > (seen = Volatile.Read(ref maxConcurrent)) && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen) { }
            try { await Respond(stream, requestLine, range, index, now); } finally { Interlocked.Decrement(ref current); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task Respond(NetworkStream stream, string requestLine, string? range, int index, int concurrent)
    {
        Targets.Enqueue(requestLine.Split(' ').ElementAtOrDefault(1) ?? "");
        LastRange = range; Ranges.Enqueue(range);
        if (RedirectOnce && Interlocked.Exchange(ref redirected, 1) == 0)
        {
            await Write(stream, $"HTTP/1.1 302 Found\r\nLocation: /moved{Path}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return;
        }
        if (CloseFrom is int close && index >= close) return;
        if (Status is int status && index >= StatusFrom)
        {
            await Write(stream, $"HTTP/1.1 {status} Status\r\nContent-Length: 0\r\nConnection: close\r\n{ExtraHeaders()}\r\n"); return;
        }
        if (Mode == "busy-extra" && concurrent > 2)
        {
            await Write(stream, "HTTP/1.1 503 Busy\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return;
        }
        long offset = 0; long? last = null;
        if (range != null)
        {
            var spec = range.Split('=')[1].Split('-');
            offset = long.Parse(spec[0]); if (spec[1].Length > 0) last = long.Parse(spec[1]);
        }
        var length = Body.Length;
        var partial = range != null && Mode is not ("ignore" or "change-full") && !(Mode == "no-ranges-on-parts" && index > 1);
        if (!partial) { offset = 0; last = null; }
        if (partial && offset >= length)
        {
            await Write(stream, $"HTTP/1.1 416 Range Not Satisfiable{Crlf}Content-Range: bytes */{length}{Crlf}Content-Length: 0{Crlf}Connection: close{Crlf}{Crlf}"); return;
        }
        var end = Math.Min(last ?? length - 1, length - 1); // inclusive
        var header = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nConnection: close\r\n";
        if (Mode is not ("unknown-length" or "overrun-range")) header += $"Content-Length: {end - offset + 1}\r\n";
        if (Mode == "last-modified") header += "Last-Modified: Thu, 01 Jan 2026 00:00:00 GMT\r\n";
        if (Mode is not ("no-validator" or "last-modified")) header += $"ETag: {(Mode is "bad-validator" or "change-full" || (Mode == "change-after-probe" && index > 1) ? "\"v2\"" : "\"v1\"")}\r\n";
        if (partial) header += $"Content-Range: bytes {(Mode == "bad-range" ? offset + 1 : offset)}-{end}/{length}\r\n";
        await Write(stream, header + ExtraHeaders() + "\r\n");
        var stopAt = Mode == "truncate" ? length / 2 : Mode == "overrun-range" ? (long)length * 2 : end + 1;
        if (Mode == "drop-part-once" && offset > 0 && Interlocked.Exchange(ref dropped, 1) == 0) stopAt = Math.Min(stopAt, offset + 65536);
        var slow = Delay > 0 && (!SlowFirstPart || (range != null && offset == 0));
        for (var i = offset; i < stopAt; i += ChunkSize)
        {
            var count = (int)Math.Min(ChunkSize, stopAt - i);
            await stream.WriteAsync(Body.AsMemory((int)(i % length), Math.Min(count, length - (int)(i % length))), stop.Token);
            if (slow) await Task.Delay(Delay, stop.Token);
        }
    }

    private const string Crlf = "\r\n";
    private string ExtraHeaders() => string.Concat(Headers.Select(h => $"{h.Key}: {h.Value}\r\n"));
    private Task Write(NetworkStream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), stop.Token).AsTask();

    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(requests); stop.Dispose(); }
}
