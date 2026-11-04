using System.Net;
using System.Text.Json;
using IDMRea.App;
using IDMRea.Contracts;
using IDMRea.Core;
using IDMRea.Engine;
using Xunit;
namespace IDMRea.Tests;

/// <summary>The engine's video commands, over the real pipe, and the browser host passing them on.</summary>
public sealed class VideoEngineTests
{
    private const string Zoo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class Runner(string stdout) : IToolRunner
    {
        private int runs;
        public int Runs => Volatile.Read(ref runs);
        public Task<ToolRun> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        { Interlocked.Increment(ref runs); return Task.FromResult(new ToolRun(0, stdout, "")); }
    }

    /// <summary>Answers nothing until the request is cancelled: a first-time setup that is still downloading.</summary>
    private sealed class Slow : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>An engine with video support on its own data folder and pipe.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly string? previous = Environment.GetEnvironmentVariable("IDMREA_DATA_DIRECTORY");
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "idmrea-video-ipc-" + Guid.NewGuid());
        public DownloadManager Manager { get; }
        public EngineClient Client { get; } = new();

        public Host(IToolRunner runner, HttpMessageHandler releases, bool ready)
        {
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", Path.Combine(Root, "data"));
            var data = Protocol.DataDirectory;
            var tools = new VideoTools(data, new HttpClient(releases), ToolSources.Official, TimeProvider.System);
            if (ready)
            {
                foreach (var file in new[] { tools.YtDlp, tools.Deno, tools.Ffmpeg }) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); }
                File.WriteAllText(Path.Combine(data, "tools", "yt-dlp.checked"), DateTimeOffset.UtcNow.ToString("O"));
            }
            var video = new VideoSupport(tools, runner, Path.Combine(data, "temp"), new HttpClient(releases));
            Manager = new DownloadManager(data, new ManagerOptions(Video: new VideoServices(video, video)));
            run = new EngineHost(Manager, new NullShell(), video).RunAsync(stop.Token);
        }

        public Task<EngineResponse> Send(EngineRequest request) => Client.SendAsync(request);

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await run; await Manager.DisposeAsync(); stop.Dispose();
            Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", previous);
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ProbeVideoReturnsChoices()
    {
        await using var host = new Host(new Runner(Fixture("youtube-zoo.json")), new Slow(), ready: true);
        var reply = await host.Send(new("probeVideo", Url: Zoo, Version: Protocol.CurrentVersion));
        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("Me at the zoo", reply.Video!.Title);
        Assert.Equal(Zoo, reply.Video.PageUrl);
        Assert.Contains(reply.Video.Choices, c => c.AudioOnly);
        Assert.Contains(reply.Video.Choices, c => c.Height == 240);
        Assert.True(reply.Tools!.Ready);
    }

    [Fact]
    public async Task ProbeVideoWhileToolsInstallReportsProgress()
    {
        var releases = new Slow();
        var runner = new Runner(Fixture("youtube-zoo.json"));
        await using var host = new Host(runner, releases, ready: false);
        var first = await host.Send(new("probeVideo", Url: Zoo, Version: Protocol.CurrentVersion));
        var second = await host.Send(new("probeVideo", Url: Zoo, Version: Protocol.CurrentVersion));
        foreach (var reply in new[] { first, second })
        {
            Assert.True(reply.Ok, reply.Error);
            Assert.Null(reply.Video);
            Assert.True(reply.Tools!.Installing);
            Assert.False(reply.Tools.Ready);
        }
        Assert.Equal(1, releases.Calls); // one setup, not one per question
        Assert.Equal(0, runner.Runs);
        var status = await host.Send(new("videoTools", Version: Protocol.CurrentVersion));
        Assert.True(status.Tools!.Installing);
    }

    [Fact]
    public async Task AddVideoAddsAJob()
    {
        await using var host = new Host(new Runner(Fixture("youtube-bbb.json")), new Slow(), ready: true);
        var reply = await host.Send(new("addVideo", Url: "https://www.youtube.com/watch?v=aqz-KE-bpKQ", Choice: "299+140",
            Folder: host.Root, StartPaused: true, Version: Protocol.CurrentVersion));
        Assert.True(reply.Ok, reply.Error);
        var job = Assert.Single(reply.Jobs!);
        Assert.Equal("299+140", job.Video!.ChoiceId);
        Assert.Equal(["299", "140"], job.Video.Streams.Select(s => s.FormatId));
        Assert.Equal(Path.Combine(host.Root, "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film (1080p).mp4"), job.Destination);

        // A plain video file the page played is added as an ordinary download.
        await using var server = new Fixture { Path = "/clip.mp4" };
        var file = await host.Send(new("addVideo", Url: server.Url, Choice: VideoSupport.FileChoice, Folder: host.Root, StartPaused: true, Version: Protocol.CurrentVersion));
        Assert.True(file.Ok, file.Error);
        var plain = Assert.Single(file.Jobs!, j => j.Url == server.Url);
        Assert.Null(plain.Video);
        Assert.Equal("clip.mp4", plain.FileName);
    }

    /// <summary>yt-dlp knows only the addresses in <paramref name="known"/>; for any other it says "Unsupported URL".</summary>
    private sealed class PickyRunner(string stdout, params string[] known) : IToolRunner
    {
        public List<string> Asked { get; } = [];
        public Task<ToolRun> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            var url = args[^1]; lock (Asked) Asked.Add(url);
            return Task.FromResult(known.Contains(url) ? new ToolRun(0, stdout, "") : new ToolRun(1, "", $"ERROR: Unsupported URL: {url}\n"));
        }
    }

    private static VideoSupport Support(string data, IToolRunner runner, HttpMessageHandler web)
    {
        var tools = new VideoTools(data, new HttpClient(new Slow()), ToolSources.Official, TimeProvider.System);
        foreach (var file in new[] { tools.YtDlp, tools.Deno, tools.Ffmpeg }) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); }
        File.WriteAllText(Path.Combine(data, "tools", "yt-dlp.checked"), DateTimeOffset.UtcNow.ToString("O"));
        return new VideoSupport(tools, runner, Path.Combine(data, "temp"), new HttpClient(web));
    }

    [Fact]
    public async Task APageYtDlpDoesNotKnowIsTriedThroughTheMediaItPlayed()
    {
        var data = Path.Combine(Path.GetTempPath(), "idmrea-video-" + Guid.NewGuid());
        try
        {
            var runner = new PickyRunner(Fixture("youtube-zoo.json"), "https://cdn.test/master.m3u8");
            var info = await Support(data, runner, new Slow()).ProbeAsync("https://shop.test/product", null,
                ["blob:https://shop.test/1", "https://cdn.test/other.mp4", "https://cdn.test/other.mp4", "https://cdn.test/master.m3u8"], default);
            Assert.Equal("https://cdn.test/master.m3u8", info.PageUrl); // the job is added for the address that worked
            Assert.Equal(["https://shop.test/product", "https://cdn.test/other.mp4", "https://cdn.test/master.m3u8"], runner.Asked); // no blob:, no repeats
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task APlainVideoFileThePagePlayedIsOffered()
    {
        var data = Path.Combine(Path.GetTempPath(), "idmrea-video-" + Guid.NewGuid());
        await using var server = new Fixture { Path = "/media/clip.mp4", BodySize = 2 * 1024 * 1024 };
        try
        {
            using var handler = new HttpClientHandler();
            var info = await Support(data, new PickyRunner(""), handler).ProbeAsync("https://shop.test/product", null, [server.Url], default);
            Assert.Equal(server.Url, info.PageUrl);
            Assert.Equal("clip", info.Title);
            Assert.Equal(new VideoChoice(VideoSupport.FileChoice, "MP4, 2.0 MB", "mp4", 2 * 1024 * 1024, null, false), Assert.Single(info.Choices));
            var none = await Assert.ThrowsAsync<VideoException>(() => Support(data, new PickyRunner(""), handler).ProbeAsync("https://shop.test/product", null, null, default));
            Assert.Equal("IDM-Rea found no video on this page.", none.Message);
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public async Task HostPassesVideoMessagesThrough()
    {
        var sent = new List<EngineRequest>();
        var info = new VideoInfo(Zoo, "Me at the zoo", 19, null, [new VideoChoice("134+140", "360p MP4, 1.2 MB", "mp4", 1_200_000, 360, false)]);
        var handler = new IDMRea.BrowserHost.HostHandler(request =>
        {
            lock (sent) sent.Add(request);
            return Task.FromResult(request.Command == "probeVideo"
                ? new EngineResponse(true, Version: Protocol.CurrentVersion, Video: info, Tools: new ToolStatus(true, "2026.10.26", false, 100, null))
                : new EngineResponse(true, [], Version: Protocol.CurrentVersion));
        });
        using var probe = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "probeVideo", id = 3, url = Zoo, referrer = "https://www.youtube.com/", cookies = "SID=abc", userAgent = "Browser/1.0",
            media = new[] { "https://cdn.test/a.m3u8", "https://cdn.test/b.mp4" },
        }));
        var answer = JsonSerializer.SerializeToElement(await handler.HandleAsync(probe.RootElement), Protocol.Json);
        Assert.True(answer.GetProperty("ok").GetBoolean(), answer.ToString());
        Assert.Equal(3, answer.GetProperty("id").GetInt32());
        Assert.Equal("Me at the zoo", answer.GetProperty("video").GetProperty("title").GetString());
        Assert.Equal("360p MP4, 1.2 MB", answer.GetProperty("video").GetProperty("choices")[0].GetProperty("label").GetString());
        Assert.True(answer.GetProperty("tools").GetProperty("ready").GetBoolean());
        var asked = sent.Single();
        Assert.Equal(("probeVideo", Zoo, "SID=abc", "Browser/1.0", "https://www.youtube.com/"), (asked.Command, asked.Url, asked.Cookies, asked.UserAgent, asked.Referrer));
        Assert.Equal(["https://cdn.test/a.m3u8", "https://cdn.test/b.mp4"], asked.Media);

        using var add = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "addVideo", id = 4, url = Zoo, choice = "134+140", cookies = "SID=abc" }));
        var added = JsonSerializer.SerializeToElement(await handler.HandleAsync(add.RootElement), Protocol.Json);
        Assert.True(added.GetProperty("ok").GetBoolean(), added.ToString());
        Assert.Equal(("addVideo", Zoo, "134+140", "SID=abc"), (sent[1].Command, sent[1].Url, sent[1].Choice, sent[1].Cookies));
    }
}
