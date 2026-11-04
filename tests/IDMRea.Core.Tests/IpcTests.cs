using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IDMRea.App;
using IDMRea.Contracts;
using IDMRea.Core;
using IDMRea.Engine;
using Xunit;
namespace IDMRea.Tests;

public sealed class IpcTests
{
    private sealed class RecordingShell : IEngineShell
    {
        public List<EngineSettings> Settings { get; } = [];
        public void Notify(DownloadJob job) { }
        public void SettingsChanged(EngineSettings settings) => Settings.Add(settings);
        public bool Recycle(string path) { File.Delete(path); return true; }
    }

    /// <summary>An engine host running in this process on its own data folder and pipe.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly string? previous = Environment.GetEnvironmentVariable("IDMREA_DATA_DIRECTORY");
        private readonly CancellationTokenSource stop = new();
        private readonly DownloadManager manager;
        private readonly Task run;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "idmrea-ipc-" + Guid.NewGuid());
        public RecordingShell Shell { get; } = new();
        public EngineClient Client { get; } = new();

        public Host()
        {
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", Path.Combine(Root, "data"));
            manager = new DownloadManager(Protocol.DataDirectory);
            run = new EngineHost(manager, Shell).RunAsync(stop.Token);
        }

        public Task<EngineResponse> Send(EngineRequest request) => Client.SendAsync(request);

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await run; await manager.DisposeAsync(); stop.Dispose();
            Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", previous);
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly NamedPipeClientStream pipe = new(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        private StreamReader reader = null!;

        public static async Task<Subscription> OpenAsync()
        {
            var s = new Subscription();
            await s.pipe.ConnectAsync(5000);
            s.reader = new StreamReader(s.pipe, new UTF8Encoding(false), false, 4096, true);
            var writer = new StreamWriter(s.pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new EngineRequest("subscribe", Version: 2), Protocol.Json));
            return s;
        }

        /// <summary>The next pushed line, or null if none arrives within <paramref name="wait"/>.</summary>
        public async Task<EngineResponse?> NextAsync(TimeSpan wait)
        {
            using var timeout = new CancellationTokenSource(wait);
            try { return JsonSerializer.Deserialize<EngineResponse>(await Protocol.ReadLineAsync(reader, 16 * 1024 * 1024, timeout.Token), Protocol.Json); }
            catch (OperationCanceledException) { return null; }
        }

        public ValueTask DisposeAsync() { reader?.Dispose(); return pipe.DisposeAsync(); }
    }

    [Fact]
    public async Task SubscribePushesFullThenDeltas()
    {
        await using var server = new Fixture();
        await using var host = new Host();
        Assert.True((await host.Send(new("list", Version: 2))).Ok);
        await using var subscription = await Subscription.OpenAsync();
        var first = await subscription.NextAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(first); Assert.True(first!.Ok); Assert.True(first.Full);
        Assert.Empty(first.Jobs!); Assert.NotNull(first.Settings); Assert.NotNull(first.Totals);

        var added = await host.Send(new("add", server.Url, host.Root, Version: 2));
        Assert.True(added.Ok, added.Error);
        var id = Assert.Single(added.Jobs!).Id;
        var sawJob = false; var finished = false;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            while (!finished)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var line = await subscription.NextAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(line); Assert.False(line!.Full);
                var job = line.Jobs?.SingleOrDefault(j => j.Id == id);
                if (job != null) { sawJob = true; finished = job.Status == DownloadStatus.Completed; }
            }
        Assert.True(sawJob);
        // A few trailing lines (final totals) are fine; then it must go quiet.
        for (var i = 0; i < 4 && await subscription.NextAsync(TimeSpan.FromMilliseconds(600)) != null; i++) { }
        Assert.Null(await subscription.NextAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task V1ListStillAnswered()
    {
        await using var host = new Host();
        var v1 = await host.Send(new("list"));
        Assert.True(v1.Ok); Assert.NotNull(v1.Jobs); Assert.NotNull(v1.Totals); Assert.NotNull(v1.Speeds);
        var future = await host.Send(new("list", Version: 3));
        Assert.False(future.Ok); Assert.Equal("Unsupported protocol version.", future.Error);
        var unknown = await host.Send(new("dance", Version: 2));
        Assert.False(unknown.Ok);
    }

    [Fact]
    public async Task BulkPauseByIds()
    {
        await using var server = new Fixture { Delay = 20 };
        await using var host = new Host();
        var a = Assert.Single((await host.Send(new("add", server.Url, host.Root, Version: 2))).Jobs!).Id;
        var b = (await host.Send(new("add", server.Url, host.Root, Version: 2))).Jobs!.Single(j => j.Id != a).Id;
        var paused = await host.Send(new("pause", Version: 2, Ids: [a, b]));
        Assert.True(paused.Ok, paused.Error);
        Assert.All(paused.Jobs!, j => Assert.Equal((DownloadStatus.Paused, PausedBy.Person), (j.Status, j.PausedBy)));
        var resumed = await host.Send(new("resume", Id: a, Version: 2));
        Assert.True(resumed.Ok);
        Assert.NotEqual(DownloadStatus.Paused, resumed.Jobs!.Single(j => j.Id == a).Status);
        Assert.Equal(DownloadStatus.Paused, resumed.Jobs!.Single(j => j.Id == b).Status);
        var all = await host.Send(new("pauseAll", Version: 2));
        Assert.All(all.Jobs!, j => Assert.Equal(PausedBy.Person, j.PausedBy));
    }

    [Fact]
    public async Task SettingsOverIpc()
    {
        await using var host = new Host();
        var set = await host.Send(new("setSettings", Version: 2, Settings: new EngineSettings(PartsPerFile: 99, DownloadsAtOnce: 2)));
        Assert.True(set.Ok, set.Error);
        Assert.Equal(16, set.Settings!.PartsPerFile);
        var read = await host.Send(new("getSettings", Version: 2));
        Assert.Equal((16, 2), (read.Settings!.PartsPerFile, read.Settings.DownloadsAtOnce));
        Assert.Equal(2, Assert.Single(host.Shell.Settings).DownloadsAtOnce);
        Assert.False((await host.Send(new("setSettings", Version: 2))).Ok);
    }

    [Fact]
    public async Task ConcurrentClientsDuringSubscribe()
    {
        await using var host = new Host();
        await using var subscription = await Subscription.OpenAsync();
        Assert.NotNull(await subscription.NextAsync(TimeSpan.FromSeconds(5)));
        await using var second = await Subscription.OpenAsync();
        Assert.NotNull(await second.NextAsync(TimeSpan.FromSeconds(5)));
        var lists = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.Send(new("list", Version: 2))));
        Assert.All(lists, l => Assert.True(l.Ok));
    }
}
