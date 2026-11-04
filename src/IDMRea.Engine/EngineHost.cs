using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IDMRea.Contracts;
using IDMRea.Core;
namespace IDMRea.Engine;

/// <summary>The engine's Windows-only side: notifications, settings that live outside the engine, the Recycle Bin, and the browser link.</summary>
public interface IEngineShell
{
    void Notify(DownloadJob job);
    void SettingsChanged(EngineSettings settings);
    bool Recycle(string path);
    /// <summary>"Add to browser": this copy of IDM-Rea becomes the one the browser extension talks to.</summary>
    void RegisterBrowser();
}

/// <summary>No tray, no notifications, no browser link; removing a finished file deletes it.</summary>
public sealed class NullShell : IEngineShell
{
    public void Notify(DownloadJob job) { }
    public void SettingsChanged(EngineSettings settings) { }
    public bool Recycle(string path) { File.Delete(path); return true; }
    public void RegisterBrowser() => throw new PlatformNotSupportedException("Connecting to a browser needs Windows.");
}

/// <summary>Serves the engine pipe: one JSON request line, one JSON reply line; <c>subscribe</c> keeps the line open for pushes.</summary>
public sealed class EngineHost(DownloadManager manager, IEngineShell shell)
{
    private static readonly TimeSpan PushEvery = TimeSpan.FromMilliseconds(250), ReadTimeout = TimeSpan.FromSeconds(15);
    private readonly object gate = new();
    private readonly List<Subscriber> subscribers = [];

    private sealed class Subscriber
    {
        public readonly HashSet<Guid> Changed = [], Removed = [];
        public bool SettingsChanged;
        public readonly SemaphoreSlim Signal = new(0, 1);
        public void Wake() { lock (Signal) if (Signal.CurrentCount == 0) Signal.Release(); }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stop);
        manager.JobChanged += OnChanged; manager.JobRemoved += OnRemoved; manager.JobFinished += shell.Notify; manager.SettingsChanged += OnSettings;
        var connections = new List<Task>();
        var pending = CreatePipe();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try { await pending.WaitForConnectionAsync(stopping.Token); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { pending.Dispose(); pending = CreatePipe(); continue; }
                // Keep a listening instance alive before serving, so rapid client reconnects
                // cannot attach to a listener that is about to be removed.
                var pipe = pending; pending = CreatePipe();
                connections.RemoveAll(c => c.IsCompleted);
                connections.Add(Task.Run(() => ServeAsync(pipe, stopping)));
            }
        }
        finally
        {
            pending.Dispose(); stopping.Cancel();
            await Task.WhenAll(connections);
            manager.JobChanged -= OnChanged; manager.JobRemoved -= OnRemoved; manager.JobFinished -= shell.Notify; manager.SettingsChanged -= OnSettings;
        }
    }

    private static NamedPipeServerStream CreatePipe() => new(Protocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private void OnChanged(DownloadJob job) { lock (gate) foreach (var s in subscribers) { lock (s) s.Changed.Add(job.Id); s.Wake(); } }
    private void OnRemoved(Guid id) { lock (gate) foreach (var s in subscribers) { lock (s) { s.Changed.Remove(id); s.Removed.Add(id); } s.Wake(); } }
    private void OnSettings(EngineSettings settings) { shell.SettingsChanged(settings); lock (gate) foreach (var s in subscribers) { lock (s) s.SettingsChanged = true; s.Wake(); } }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationTokenSource stopping)
    {
        await using var _ = pipe;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            // Only reading the request is timed here: a link check has its own limit (Protocol.ProbeLimit), and an
            // add that already happened must still be answered, or the app would offer to add it again.
            string line;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
            { timeout.CancelAfter(ReadTimeout); line = await Protocol.ReadLineAsync(reader, 65536, timeout.Token); }
            EngineRequest? request = null; EngineResponse response;
            try
            {
                request = JsonSerializer.Deserialize<EngineRequest>(line, Protocol.Json) ?? throw new InvalidDataException("Missing request.");
                if (request.Version is not (1 or 2)) throw new InvalidDataException("Unsupported protocol version.");
                if (request.Command == "subscribe") { await SubscribeAsync(reader, writer, stopping.Token); return; }
                response = await HandleAsync(request, stopping.Token);
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested) { response = new(false, Error: ErrorText(ex), Version: Protocol.CurrentVersion); }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, Protocol.Json).AsMemory(), stopping.Token);
            if (request?.Command == "shutdown" && response.Ok) stopping.Cancel();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static string ErrorText(Exception ex)
    {
        var kind = RetryPolicy.Classify(ex);
        int? status = ex switch { TransferException { HttpStatus: int s } => s, HttpRequestException { StatusCode: { } code } => (int)code, _ => null };
        // Network, timeout and disk causes get the plain-language text; our own validation messages are already plain.
        return ex is TransferException or HttpRequestException || kind is ErrorKind.DiskFull or ErrorKind.AccessDenied or ErrorKind.Timeout ? Text.Describe(kind, status) : ex.Message;
    }

    private async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken token)
    {
        IEnumerable<Guid> Targets() => request.Ids is { Count: > 0 } ids ? ids : [request.Id ?? throw new ArgumentException("Missing job ID.")];
        ProbeResult? probe = null; EngineSettings? settings = null;
        switch (request.Command)
        {
            case "list": break;
            case "probe": probe = await manager.ProbeAsync(request.Url ?? throw new ArgumentException("Missing URL."), token); break;
            case "add":
                await manager.AddAsync(request.Url ?? "", request.Folder, request.FileName, request.StartPaused,
                    new RequestHeaders(request.Referrer, request.Cookies, request.UserAgent, request.Url), request.ExpectedSha256);
                break;
            case "pause": foreach (var id in Targets()) await manager.ControlAsync(id); break;
            case "resume": foreach (var id in Targets()) await manager.ControlAsync(id, resume: true); break;
            case "cancel": foreach (var id in Targets()) await manager.ControlAsync(id, cancel: true); break;
            case "retry": foreach (var id in Targets()) await manager.RetryAsync(id); break;
            case "remove": foreach (var id in Targets()) await manager.RemoveAsync(id, request.DeleteFile); break;
            case "pauseAll": await manager.PauseAllAsync(PausedBy.Person); break;
            case "resumeAll": await manager.ResumeAllAsync(); break;
            case "getSettings": settings = manager.Settings; break;
            case "setSettings":
                await manager.ApplySettingsAsync(request.Settings ?? throw new ArgumentException("Missing settings."));
                settings = manager.Settings; break;
            case "registerBrowser": shell.RegisterBrowser(); break;
            case "shutdown": break;
            default: throw new ArgumentException("Unknown command.");
        }
        return new(true, manager.Snapshot(), Version: Protocol.CurrentVersion, Speeds: manager.Speeds(), Totals: manager.Totals(), Settings: settings, Probe: probe);
    }

    /// <summary>First a full picture, then at most one line every 250 ms with what changed. Quiet while nothing changes.</summary>
    private async Task SubscribeAsync(StreamReader reader, StreamWriter writer, CancellationToken stop)
    {
        var me = new Subscriber();
        lock (gate) subscribers.Add(me);
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(stop);
        // The client sends nothing more; a finished read means it went away.
        _ = reader.ReadAsync(new char[1].AsMemory(), closed.Token).AsTask().ContinueWith(_ => closed.Cancel(), TaskScheduler.Default);
        try
        {
            async Task Send(EngineResponse line) => await writer.WriteLineAsync(JsonSerializer.Serialize(line, Protocol.Json).AsMemory(), closed.Token);
            var totals = manager.Totals(); var speeds = manager.Speeds();
            await Send(new(true, manager.Snapshot(), Version: Protocol.CurrentVersion, Full: true, Speeds: speeds, Totals: totals, Settings: manager.Settings));
            while (true)
            {
                await Task.Delay(PushEvery, closed.Token);
                bool quiet; lock (me) quiet = me.Changed.Count == 0 && me.Removed.Count == 0 && !me.SettingsChanged;
                if (quiet && totals.Active == 0 && manager.Totals() == totals) { await me.Signal.WaitAsync(closed.Token); continue; }
                Guid[] changed, removed; bool settingsChanged;
                lock (me)
                {
                    changed = [.. me.Changed]; removed = [.. me.Removed]; settingsChanged = me.SettingsChanged;
                    me.Changed.Clear(); me.Removed.Clear(); me.SettingsChanged = false;
                }
                var nextTotals = manager.Totals(); var nextSpeeds = manager.Speeds();
                if (changed.Length == 0 && removed.Length == 0 && !settingsChanged && nextTotals == totals && SameSpeeds(speeds, nextSpeeds)) continue;
                totals = nextTotals; speeds = nextSpeeds;
                var jobs = manager.Snapshot().Where(j => changed.Contains(j.Id)).ToArray();
                await Send(new(true, jobs, Version: Protocol.CurrentVersion, Removed: removed.Length > 0 ? removed : null, Speeds: speeds, Totals: totals,
                    Settings: settingsChanged ? manager.Settings : null));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { lock (gate) subscribers.Remove(me); }
    }

    private static bool SameSpeeds(IReadOnlyDictionary<Guid, long> a, IReadOnlyDictionary<Guid, long> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
}
