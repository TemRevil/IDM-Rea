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
    private readonly EngineSettings settings;
    private readonly RateLimiter limiter = new();
    private readonly SpeedMeter meter = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(30), MaxAutomaticRedirections = 10 }) { Timeout = Timeout.InfiniteTimeSpan };
    public DownloadManager(string directory)
    {
        store = new(directory);
        settings = store.LoadSettings(); limiter.BytesPerSecond = settings.SpeedLimitBytesPerSecond;
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
    public static string SafeName(string name) => FileNames.SafeName(name);
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
        Save(Get(id) with { Status = DownloadStatus.Queued, Error = null, ErrorKind = ErrorKind.None, HttpStatus = null });
        var cancel = new CancellationTokenSource();
        workers[id] = (cancel, Task.Run(() => TransferAsync(id, cancel.Token)));
    }
    public async Task ControlAsync(Guid id, bool cancel = false, bool resume = false)
    {
        await controls.WaitAsync();
        try
        {
            var job = Get(id);
            // Resume is idempotent for queued/active/terminal jobs. In particular,
            // a repeated UI or IPC command must not cancel a running transfer.
            if (resume)
            {
                if (job.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
                if (File.Exists(job.Destination) || Directory.Exists(job.Destination))
                    throw new IOException("The destination already exists. Move it before resuming.");
            }
            else if (!cancel && job.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading)) return;
            if (workers.Remove(id, out var worker)) { worker.Cancel.Cancel(); await worker.Task; worker.Cancel.Dispose(); }
            job = Get(id);
            if (job.Status == DownloadStatus.Completed) return;
            if (cancel) { File.Delete(job.PartialPath); Save(job with { Status = DownloadStatus.Cancelled, ReceivedBytes = 0, Parts = null, Error = null }); }
            else if (resume) { if (job.Status != DownloadStatus.Cancelled) Start(id); }
            else if (job.Status != DownloadStatus.Cancelled) Save(job with { Status = DownloadStatus.Paused });
        }
        finally { controls.Release(); }
    }
    private TransferContext Context() => new(http, limiter, meter, () => settings, Save);
    private static ProbeResult SavedProbe(DownloadJob job) => new(job.Url, job.ResolvedUrl ?? job.Url, job.FileName, job.TotalBytes,
        true, job.ETag, job.LastModified, null, job.Category, true);
    private async Task TransferAsync(Guid id, CancellationToken token)
    {
        var acquired = false; var job = Get(id);
        try
        {
            await slots.WaitAsync(token); acquired = true;
            job = Get(id) with { Status = DownloadStatus.Downloading }; Save(job);
            var context = Context();
            if (job.Parts is { Count: > 0 } && job.Resumable && job.TotalBytes > 0)
                job = await MultiPartTransfer.RunAsync(job, SavedProbe(job), context, token);
            else if (job.ReceivedBytes == 0 && job.Parts == null)
            {
                var probe = await Prober.ProbeAsync(http, job.Url, token);
                job = job with { ResolvedUrl = probe.ResolvedUrl }; Save(job);
                job = probe.Resumable && probe.TotalBytes > 0
                    ? await MultiPartTransfer.RunAsync(job, probe, context, token)
                    : await SingleStreamTransfer.RunAsync(job, context, token);
            }
            else job = await SingleStreamTransfer.RunAsync(job, context, token);
            await using (var input = File.OpenRead(job.PartialPath)) job = job with { CompletionSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, token)), TotalBytes = job.ReceivedBytes, Status = DownloadStatus.Finalizing };
            Save(job); File.Move(job.PartialPath, job.Destination, false); Save(job with { Status = DownloadStatus.Completed });
        }
        catch (Exception ex)
        {
            // Recover the latest durable checkpoint, including any publication identity.
            job = Get(id);
            if (job.Status == DownloadStatus.Finalizing) Save(RecoverPublication(job));
            else if (token.IsCancellationRequested) Save(job with { Status = DownloadStatus.Paused, Error = null });
            else Save(job with { Status = DownloadStatus.Failed, Error = ex.Message, ErrorKind = RetryPolicy.Classify(ex),
                HttpStatus = ex is TransferException { HttpStatus: int status } ? status : ex is HttpRequestException { StatusCode: { } code } ? (int)code : null });
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
            if (Matches(job.Destination)) return job with { Status = DownloadStatus.Completed, TotalBytes = job.ReceivedBytes, Error = null };
            if (File.Exists(job.Destination)) throw new IOException("Destination is occupied; existing file preserved.");
            if (!Matches(job.PartialPath)) throw new IOException("Publication checkpoint does not match the partial file.");
            File.Move(job.PartialPath, job.Destination, false); return job with { Status = DownloadStatus.Completed, TotalBytes = job.ReceivedBytes, Error = null };
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
