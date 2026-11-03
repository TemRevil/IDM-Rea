using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IDMRea.Contracts;

namespace IDMRea.Core;
public sealed class DownloadManager : IAsyncDisposable
{
    private readonly JobStore store;
    private readonly Dictionary<Guid, DownloadJob> jobs = [];
    private readonly Dictionary<Guid, (CancellationTokenSource Cancel, Task Task)> workers = [];
    private readonly SemaphoreSlim controls = new(1);
    private readonly SemaphoreSlim slots = new(3);
    private readonly object gate = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(30), MaxAutomaticRedirections = 10 }) { Timeout = Timeout.InfiniteTimeSpan };
    public DownloadManager(string directory)
    {
        store = new(directory);
        foreach (var original in store.Load())
        {
            var job = original;
            if (job.Status == DownloadStatus.Finalizing) job = RecoverPublication(job);
            else if (job.Status is DownloadStatus.Downloading or DownloadStatus.Queued) job = job with { Status = DownloadStatus.Paused };
            Save(job);
        }
    }
    private void Save(DownloadJob job) { lock (gate) { job = job with { UpdatedAt = DateTimeOffset.UtcNow }; store.Save(job); jobs[job.Id] = job; } }
    private DownloadJob Get(Guid id) { lock (gate) return jobs[id]; }
    public IReadOnlyList<DownloadJob> Snapshot() { lock (gate) return jobs.Values.OrderByDescending(j => j.UpdatedAt).ToArray(); }
    public static string SafeName(string name)
    {
        name = new string(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) name = "_" + name;
        return name.Length > 180 ? name[..180] : name;
    }
    public async Task<DownloadJob> AddAsync(string url, string folder)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Use an absolute HTTP or HTTPS URL.");
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Use an absolute destination folder.");
        await controls.WaitAsync();
        try
        {
            Directory.CreateDirectory(folder); var name = SafeName(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()));
            var destination = Path.Combine(folder, name); var n = 1;
            while (File.Exists(destination) || Directory.Exists(destination) || Snapshot().Any(j => j.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)))
                destination = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n++}){Path.GetExtension(name)}");
            var id = Guid.NewGuid(); var job = new DownloadJob(id, uri.AbsoluteUri, destination, destination + "." + id.ToString("N") + ".part", DownloadStatus.Queued);
            Save(job); Start(id); return job;
        }
        finally { controls.Release(); }
    }
    private void Start(Guid id)
    {
        Save(Get(id) with { Status = DownloadStatus.Queued, Error = null });
        var cancel = new CancellationTokenSource();
        workers[id] = (cancel, Task.Run(() => TransferAsync(id, cancel.Token)));
    }
    public async Task ControlAsync(Guid id, bool cancel = false, bool resume = false)
    {
        await controls.WaitAsync();
        try
        {
            var job = Get(id);
            if (workers.Remove(id, out var worker)) { worker.Cancel.Cancel(); await worker.Task; worker.Cancel.Dispose(); }
            job = Get(id);
            if (job.Status == DownloadStatus.Completed) return;
            if (cancel) { File.Delete(job.PartialPath); Save(job with { Status = DownloadStatus.Cancelled }); }
            else if (resume) { if (job.Status != DownloadStatus.Cancelled) Start(id); }
            else if (job.Status != DownloadStatus.Cancelled) Save(job with { Status = DownloadStatus.Paused });
        }
        finally { controls.Release(); }
    }
    private static bool Strong(string? tag) => EntityTagHeaderValue.TryParse(tag, out var parsed) && !parsed.IsWeak;
    private async Task TransferAsync(Guid id, CancellationToken token)
    {
        var acquired = false; var job = Get(id);
        try
        {
            await slots.WaitAsync(token); acquired = true;
            job = Get(id) with { Status = DownloadStatus.Downloading }; Save(job);
            var size = File.Exists(job.PartialPath) ? new FileInfo(job.PartialPath).Length : 0;
            var offset = Math.Min(size, job.ReceivedBytes);
            if ((!Strong(job.ETag) && job.LastModified == null) || job.TotalBytes == offset) offset = 0;
            using var file = new FileStream(job.PartialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 65536, true);
            file.SetLength(offset); file.Position = offset;
            using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = Strong(job.ETag) ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(job.ETag!)) : new RangeConditionHeaderValue(job.LastModified!.Value);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
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
            job = job with { ReceivedBytes = offset, TotalBytes = total, ETag = etag, LastModified = modified, CompletionSha256 = null }; Save(job);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[65536]; var checkpoint = DateTime.UtcNow;
            try
            {
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var read = await body.ReadAsync(buffer, idle.Token); if (read == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, read), token); job = job with { ReceivedBytes = job.ReceivedBytes + read };
                    if (DateTime.UtcNow - checkpoint > TimeSpan.FromMilliseconds(500)) { file.Flush(true); Save(job); checkpoint = DateTime.UtcNow; }
                }
            }
            finally { file.Flush(true); Save(job); }
            if (job.TotalBytes is long expected && expected != job.ReceivedBytes) throw new IOException("The response ended before the expected size.");
            file.Close();
            await using (var input = File.OpenRead(job.PartialPath)) job = job with { CompletionSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, token)), Status = DownloadStatus.Finalizing };
            Save(job); File.Move(job.PartialPath, job.Destination, false); Save(job with { Status = DownloadStatus.Completed });
        }
        catch (Exception ex)
        {
            // Recover the latest durable checkpoint, including any publication identity.
            job = Get(id);
            if (job.Status == DownloadStatus.Finalizing) Save(RecoverPublication(job));
            else Save(job with { Status = token.IsCancellationRequested ? DownloadStatus.Paused : DownloadStatus.Failed, Error = token.IsCancellationRequested ? null : ex.Message });
        }
        finally { if (acquired) slots.Release(); }
    }
    private static DownloadJob RecoverPublication(DownloadJob job)
    {
        bool Matches(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != job.ReceivedBytes || job.CompletionSha256 == null) return false;
            using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)) == job.CompletionSha256;
        }
        try
        {
            if (Matches(job.Destination)) return job with { Status = DownloadStatus.Completed, Error = null };
            if (File.Exists(job.Destination)) throw new IOException("Destination is occupied; existing file preserved.");
            if (!Matches(job.PartialPath)) throw new IOException("Publication checkpoint does not match the partial file.");
            File.Move(job.PartialPath, job.Destination, false); return job with { Status = DownloadStatus.Completed, Error = null };
        }
        catch (Exception ex) { return job with { Status = DownloadStatus.Failed, Error = ex.Message }; }
    }
    public async ValueTask DisposeAsync()
    {
        await controls.WaitAsync();
        try { foreach (var worker in workers.Values) worker.Cancel.Cancel(); await Task.WhenAll(workers.Values.Select(w => w.Task)); foreach (var w in workers.Values) w.Cancel.Dispose(); workers.Clear(); http.Dispose(); }
        finally { controls.Release(); }
    }
}
