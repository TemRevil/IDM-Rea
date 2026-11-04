using System.Net;
using System.Net.NetworkInformation;
using IDMRea.Contracts;

namespace IDMRea.Core;

/// <summary>Hooks a host or test can supply. <paramref name="Recycle"/> removes a finished file (default: delete);
/// <paramref name="WriteFault"/> is a test seam called before each write with its file offset.
/// <paramref name="FreeSpace"/> replaces the drive's free bytes for a path (test seam).</summary>
public sealed record ManagerOptions(Func<string, bool>? Recycle = null, TimeProvider? Time = null, HttpMessageHandler? Handler = null,
    Func<long, Exception?>? WriteFault = null, Func<string, long>? FreeSpace = null);

public sealed class DownloadManager : IAsyncDisposable
{
    private sealed record Worker(CancellationTokenSource Cancel, Task Task, SpeedMeter Meter);

    private readonly JobStore store;
    private readonly ManagerOptions options;
    private readonly TimeProvider time;
    private readonly Dictionary<Guid, DownloadJob> jobs = [];
    private readonly Dictionary<Guid, Worker> workers = [];
    private readonly Dictionary<Guid, ITimer> timers = [];
    private readonly Dictionary<Guid, ProbeResult> probes = [];
    private readonly SemaphoreSlim controls = new(1);
    private readonly object gate = new();
    private readonly ConcurrencyGate slots;
    private readonly RateLimiter limiter = new();
    private readonly HttpClient http;
    private EngineSettings settings;
    private bool disposed, watchingNetwork;

    public event Action<DownloadJob>? JobChanged;
    public event Action<Guid>? JobRemoved;
    /// <summary>Raised after new settings are saved, from the app or from inside the engine (the tray menu).</summary>
    public event Action<EngineSettings>? SettingsChanged;
    /// <summary>Raised once when a job becomes Completed or Failed.</summary>
    public event Action<DownloadJob>? JobFinished;

    public DownloadManager(string directory, ManagerOptions? options = null)
    {
        this.options = options ?? new();
        time = this.options.Time ?? TimeProvider.System;
        store = new(directory);
        settings = store.LoadSettings();
        slots = new(settings.DownloadsAtOnce);
        limiter.BytesPerSecond = settings.SpeedLimitBytesPerSecond;
        http = new(this.options.Handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(30), MaxAutomaticRedirections = 10 })
        { Timeout = Timeout.InfiniteTimeSpan };

        var start = new List<Guid>(); var wait = new List<DownloadJob>();
        foreach (var original in store.Load())
        {
            var job = original;
            switch (job.Status)
            {
                case DownloadStatus.Finalizing: job = RecoverPublication(job); break;
                case DownloadStatus.Downloading or DownloadStatus.Queued: start.Add(job.Id); break;
                case DownloadStatus.Paused when job.PausedBy == PausedBy.Engine: start.Add(job.Id); break;
                case DownloadStatus.Retrying: wait.Add(job); break;
            }
            lock (gate) jobs[job.Id] = job;
            if (!ReferenceEquals(job, original)) Save(job);
        }
        foreach (var id in start) Start(id);
        foreach (var job in wait) Schedule(job.Id, job.NextRetryAt ?? time.GetUtcNow());
        try { NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged; watchingNetwork = true; }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException) { }
    }

    public EngineSettings Settings { get { lock (gate) return settings; } }
    public IReadOnlyList<DownloadJob> Snapshot() { lock (gate) return jobs.Values.OrderByDescending(j => j.UpdatedAt).ToArray(); }
    public static string SafeName(string name) => FileNames.SafeName(name);

    public IReadOnlyDictionary<Guid, long> Speeds()
    {
        lock (gate)
            return workers.Where(w => !w.Value.Task.IsCompleted && jobs.TryGetValue(w.Key, out var j) && j.Status == DownloadStatus.Downloading)
                .ToDictionary(w => w.Key, w => w.Value.Meter.BytesPerSecond());
    }

    public EngineTotals Totals()
    {
        var speeds = Speeds();
        lock (gate) return new(jobs.Values.Count(j => j.Status == DownloadStatus.Downloading), speeds.Values.Sum());
    }

    private DownloadJob Get(Guid id) { lock (gate) return jobs[id]; }

    private DownloadJob Save(DownloadJob job)
    {
        bool finished;
        lock (gate)
        {
            job = job with { UpdatedAt = time.GetUtcNow() };
            finished = job.Status is DownloadStatus.Completed or DownloadStatus.Failed && (!jobs.TryGetValue(job.Id, out var before) || before.Status != job.Status);
            store.Save(job); jobs[job.Id] = job;
        }
        JobChanged?.Invoke(job);
        if (finished) JobFinished?.Invoke(job);
        return job;
    }

    public Task<ProbeResult> ProbeAsync(string url, CancellationToken token = default) => Prober.ProbeAsync(http, url, token);

    public async Task<DownloadJob> AddAsync(string url, string folder, string? fileName = null, bool startPaused = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Use an absolute HTTP or HTTPS URL.");
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Use an absolute destination folder.");
        var probe = await ProbeAsync(uri.AbsoluteUri);
        await controls.WaitAsync();
        try
        {
            Directory.CreateDirectory(folder);
            var name = SafeName(string.IsNullOrWhiteSpace(fileName) ? probe.FileName : Text.NormalizeDigits(fileName.Trim()));
            var destination = FileNames.UniquePath(folder, name, path => File.Exists(path) || Directory.Exists(path)
                || Snapshot().Any(j => j.Destination.Equals(path, StringComparison.OrdinalIgnoreCase)));
            var id = Guid.NewGuid();
            Save(new DownloadJob(id, uri.AbsoluteUri, destination, destination + "." + id.ToString("N") + ".part",
                startPaused ? DownloadStatus.Paused : DownloadStatus.Queued, TotalBytes: probe.TotalBytes, ETag: probe.ETag,
                LastModified: probe.LastModified, Category: probe.Category, CreatedAt: time.GetUtcNow(),
                PausedBy: startPaused ? PausedBy.Person : PausedBy.None, Resumable: probe.Resumable, ResolvedUrl: probe.ResolvedUrl));
            if (!startPaused) { lock (gate) probes[id] = probe; Start(id); }
            return Get(id);
        }
        finally { controls.Release(); }
    }

    /// <summary>Starts a job's worker. Callers hold <see cref="controls"/> (or are the constructor).</summary>
    private void Start(Guid id)
    {
        lock (gate) if (timers.Remove(id, out var timer)) timer.Dispose();
        Save(Get(id) with { Status = DownloadStatus.Queued, PausedBy = PausedBy.None, Error = null, ErrorKind = ErrorKind.None, HttpStatus = null, NextRetryAt = null });
        var cancel = new CancellationTokenSource(); var meter = new SpeedMeter();
        lock (gate) workers[id] = new Worker(cancel, Task.Run(() => TransferAsync(id, cancel, meter)), meter);
    }

    /// <summary>Stops a job's worker and retry timer and waits for the worker to save its last checkpoint.</summary>
    private async Task StopAsync(params Guid[] ids)
    {
        var stopping = new List<Worker>();
        lock (gate)
            foreach (var id in ids)
            {
                if (timers.Remove(id, out var timer)) timer.Dispose();
                if (workers.Remove(id, out var worker)) stopping.Add(worker);
            }
        foreach (var worker in stopping) worker.Cancel.Cancel();
        await Task.WhenAll(stopping.Select(w => w.Task));
        foreach (var worker in stopping) worker.Cancel.Dispose();
    }

    private void Schedule(Guid id, DateTimeOffset at)
    {
        var due = at - time.GetUtcNow();
        if (due <= TimeSpan.Zero) { _ = RetryDueAsync(id); return; }
        var timer = time.CreateTimer(_ => _ = RetryDueAsync(id), null, due, Timeout.InfiniteTimeSpan);
        lock (gate) { if (timers.Remove(id, out var old)) old.Dispose(); timers[id] = timer; }
    }

    private async Task RetryDueAsync(Guid id)
    {
        await controls.WaitAsync();
        try
        {
            if (disposed) return;
            DownloadJob? job;
            lock (gate) { if (timers.Remove(id, out var timer)) timer.Dispose(); jobs.TryGetValue(id, out job); }
            if (job?.Status == DownloadStatus.Retrying) Start(id);
        }
        finally { controls.Release(); }
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        IEnumerable<Guid> waiting; lock (gate) waiting = jobs.Values.Where(j => j.Status == DownloadStatus.Retrying).Select(j => j.Id).ToArray();
        foreach (var id in waiting) _ = RetryDueAsync(id);
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
                EnsureDestinationFree(job);
            }
            else if (!cancel && job.Status is not (DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Retrying)) return;
            await StopAsync(id);
            job = Get(id);
            if (job.Status == DownloadStatus.Completed) return;
            if (cancel)
            {
                File.Delete(job.PartialPath);
                Save(job with { Status = DownloadStatus.Cancelled, ReceivedBytes = 0, Parts = null, Error = null, NextRetryAt = null });
            }
            else if (resume) { if (job.Status != DownloadStatus.Cancelled) { if (job.Status == DownloadStatus.Failed) PrepareRetry(job); Start(id); } }
            else if (job.Status != DownloadStatus.Cancelled) Save(job with { Status = DownloadStatus.Paused, PausedBy = PausedBy.Person, NextRetryAt = null });
        }
        finally { controls.Release(); }
    }

    /// <summary>Failed, cancelled or waiting: start again now. Continues from saved bytes when the file can resume
    /// and did not change on the server; otherwise starts from zero.</summary>
    public async Task RetryAsync(Guid id)
    {
        await controls.WaitAsync();
        try
        {
            var job = Get(id);
            if (job.Status is not (DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.Retrying)) return;
            EnsureDestinationFree(job);
            await StopAsync(id);
            PrepareRetry(Get(id)); Start(id);
        }
        finally { controls.Release(); }
    }

    private void PrepareRetry(DownloadJob job)
    {
        var canResume = job.ErrorKind != ErrorKind.Changed && File.Exists(job.PartialPath)
            && (job.Parts is { Count: > 0 } || job.ReceivedBytes > 0)
            && (job.Resumable || SingleStreamTransfer.Strong(job.ETag) || job.LastModified != null);
        if (!canResume)
        {
            if (File.Exists(job.PartialPath)) File.Delete(job.PartialPath);
            job = job with { ReceivedBytes = 0, Parts = null, TotalBytes = null, ETag = null, LastModified = null, CompletionFileId = null };
        }
        Save(job with { Attempts = 0 });
    }

    private static void EnsureDestinationFree(DownloadJob job)
    {
        if (File.Exists(job.Destination) || Directory.Exists(job.Destination))
            throw new IOException("The destination already exists. Move it before resuming.");
    }

    public async Task RemoveAsync(Guid id, bool deleteFile)
    {
        await controls.WaitAsync();
        try
        {
            lock (gate) if (!jobs.ContainsKey(id)) return;
            await StopAsync(id);
            var job = Get(id);
            if (File.Exists(job.PartialPath)) File.Delete(job.PartialPath);
            if (deleteFile && job.Status == DownloadStatus.Completed && File.Exists(job.Destination) && !Recycle(job.Destination))
                throw new IOException("Couldn't move the file to the Recycle Bin.");
            lock (gate) { store.Delete(id); jobs.Remove(id); probes.Remove(id); }
            JobRemoved?.Invoke(id);
        }
        finally { controls.Release(); }
    }

    private bool Recycle(string path)
    {
        if (options.Recycle != null) return options.Recycle(path);
        File.Delete(path); return true;
    }

    /// <summary>Pauses everything waiting or running. Paused by the person also covers jobs the engine paused,
    /// so nothing restarts by itself after a Quit.</summary>
    public async Task PauseAllAsync(PausedBy by)
    {
        await controls.WaitAsync();
        try
        {
            Guid[] ids; lock (gate) ids = jobs.Values.Where(j => j.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Retrying
                || (by == PausedBy.Person && j is { Status: DownloadStatus.Paused, PausedBy: PausedBy.Engine })).Select(j => j.Id).ToArray();
            await StopAsync(ids);
            foreach (var id in ids)
                if (Get(id) is { Status: not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled) } job)
                    Save(job with { Status = DownloadStatus.Paused, PausedBy = by, NextRetryAt = null });
        }
        finally { controls.Release(); }
    }

    public async Task ResumeAllAsync()
    {
        await controls.WaitAsync();
        try
        {
            DownloadJob[] paused; lock (gate) paused = jobs.Values.Where(j => j.Status == DownloadStatus.Paused).ToArray();
            foreach (var job in paused.Where(j => !File.Exists(j.Destination) && !Directory.Exists(j.Destination))) Start(job.Id);
        }
        finally { controls.Release(); }
    }

    public async Task ApplySettingsAsync(EngineSettings next)
    {
        next = next.Clamped();
        await controls.WaitAsync();
        try
        {
            store.SaveSettings(next);
            lock (gate) settings = next;
            slots.Limit = next.DownloadsAtOnce;
            limiter.BytesPerSecond = next.SpeedLimitBytesPerSecond;
        }
        finally { controls.Release(); }
        SettingsChanged?.Invoke(next);
    }

    private static ProbeResult SavedProbe(DownloadJob job) => new(job.Url, job.ResolvedUrl ?? job.Url, job.FileName, job.TotalBytes,
        job.ETag, job.LastModified, null, job.Category, true);

    /// <summary>
    /// A redirect target can expire (signed links), so a resumed download asks its original URL again. The same file
    /// gets the fresh target (every ranged response is still checked against the saved size and validator); no answer
    /// keeps the saved target.
    /// </summary>
    private async Task<DownloadJob> RefreshLinkAsync(DownloadJob job, CancellationToken token)
    {
        if (job.ResolvedUrl == null || job.ResolvedUrl == job.Url) return job;
        ProbeResult fresh;
        try { fresh = await ProbeAsync(job.Url, token); }
        catch (Exception) when (!token.IsCancellationRequested) { return job; }
        return fresh.ResolvedUrl == job.ResolvedUrl ? job : Save(job with { ResolvedUrl = fresh.ResolvedUrl });
    }

    private async Task TransferAsync(Guid id, CancellationTokenSource cancel, SpeedMeter meter)
    {
        var token = cancel.Token; IDisposable? lease = null;
        try
        {
            lease = await slots.EnterAsync(token);
            var job = Save(Get(id) with { Status = DownloadStatus.Downloading });
            var startBytes = job.ReceivedBytes;
            // New bytes mean the connection works again, so the retry count starts over.
            var context = new TransferContext(http, limiter, meter, () => Settings,
                j => Save(j.ReceivedBytes > startBytes && j.Attempts != 0 ? j with { Attempts = 0 } : j), options.WriteFault, options.FreeSpace);
            if (job.ReceivedBytes > 0 || job.Parts != null) job = await RefreshLinkAsync(job, token);
            if (job.Parts is { Count: > 0 } && job.Resumable && job.TotalBytes > 0)
                job = await MultiPartTransfer.RunAsync(job, SavedProbe(job), context, token);
            else if (job.ReceivedBytes == 0 && job.Parts == null)
            {
                ProbeResult? probe; lock (gate) probes.Remove(id, out probe);
                probe ??= await ProbeAsync(job.Url, token);
                job = Save(job with { ResolvedUrl = probe.ResolvedUrl, Resumable = probe.Resumable });
                job = probe.Resumable && probe.TotalBytes > 0
                    ? await MultiPartTransfer.RunAsync(job, probe, context, token)
                    : await SingleStreamTransfer.RunAsync(job, context, token);
            }
            else job = await SingleStreamTransfer.RunAsync(job, context, token);
            job = job with { CompletionFileId = FileIdentity.Of(job.PartialPath), TotalBytes = job.ReceivedBytes, Status = DownloadStatus.Finalizing };
            ZoneMark.Write(job.PartialPath, job.Url); // the stream moves with the file
            Save(job); File.Move(job.PartialPath, job.Destination, false);
            Save(job with { Status = DownloadStatus.Completed, CompletedAt = time.GetUtcNow(), Attempts = 0, Error = null });
        }
        catch (Exception ex)
        {
            // Recover the latest durable checkpoint, including any publication identity.
            var job = Get(id);
            if (job.Status == DownloadStatus.Finalizing) Save(RecoverPublication(job));
            else if (token.IsCancellationRequested) Save(job with { Status = DownloadStatus.Paused, PausedBy = PausedBy.Engine, Error = null });
            else Fail(job, ex);
        }
        finally
        {
            lease?.Dispose();
            lock (gate) if (workers.TryGetValue(id, out var current) && current.Cancel == cancel) { workers.Remove(id); cancel.Dispose(); }
        }
    }

    private void Fail(DownloadJob job, Exception ex)
    {
        var kind = RetryPolicy.Classify(ex);
        int? status = ex switch { TransferException { HttpStatus: int s } => s, HttpRequestException { StatusCode: { } code } => (int)code, _ => null };
        var attempts = job.Attempts; // already 0 if this run saved new bytes
        var message = Text.Describe(kind, status);
        if (RetryPolicy.IsRetryable(kind) && attempts < Settings.RetryAttempts)
        {
            var at = time.GetUtcNow() + RetryPolicy.Delay(attempts + 1, (ex as TransferException)?.RetryAfter);
            Save(job with { Status = DownloadStatus.Retrying, Attempts = attempts + 1, NextRetryAt = at, ErrorKind = kind, HttpStatus = status, Error = message });
            Schedule(job.Id, at);
        }
        else Save(job with { Status = DownloadStatus.Failed, Attempts = attempts, NextRetryAt = null, ErrorKind = kind, HttpStatus = status, Error = message });
    }

    private static DownloadJob RecoverPublication(DownloadJob job)
    {
        bool Matches(string path)
        {
            // The file ID stays with the file through the move; without one (other systems) the size has to do.
            return File.Exists(path) && new FileInfo(path).Length == job.ReceivedBytes
                && (job.CompletionFileId == null || FileIdentity.Of(path) == job.CompletionFileId);
        }
        try
        {
            if (Matches(job.Destination)) return job with { Status = DownloadStatus.Completed, TotalBytes = job.ReceivedBytes, Error = null };
            // Another file took the name: it is left alone. The finished file is gone: Retry finds no part file and starts over.
            if (File.Exists(job.Destination)) return Failed(ErrorKind.NameTaken);
            if (!Matches(job.PartialPath)) return Failed(ErrorKind.Lost);
            File.Move(job.PartialPath, job.Destination, false); return job with { Status = DownloadStatus.Completed, TotalBytes = job.ReceivedBytes, Error = null };
        }
        catch (Exception ex) { return job with { Status = DownloadStatus.Failed, ErrorKind = RetryPolicy.Classify(ex), Error = Text.Describe(RetryPolicy.Classify(ex), null) }; }
        DownloadJob Failed(ErrorKind kind) => job with { Status = DownloadStatus.Failed, ErrorKind = kind, Error = Text.Describe(kind, null) };
    }

    /// <summary>Running jobs are saved as paused by the engine, so they carry on at the next start.</summary>
    public async ValueTask DisposeAsync()
    {
        await controls.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (watchingNetwork) NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            Guid[] ids; lock (gate) { foreach (var timer in timers.Values) timer.Dispose(); timers.Clear(); ids = workers.Keys.ToArray(); }
            await StopAsync(ids);
            http.Dispose();
        }
        finally { controls.Release(); }
    }
}
