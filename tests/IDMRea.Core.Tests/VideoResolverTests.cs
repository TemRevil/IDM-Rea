using System.Text.Json.Nodes;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class VideoResolverTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "idmrea-resolver-" + Guid.NewGuid());
    public VideoResolverTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private VideoResolver Resolver(FakeRunner runner) => new(runner, dir, @"C:\tools\yt-dlp.exe", @"C:\tools\deno\deno.exe");

    [Fact]
    public async Task ChoicesPreferAvcThenAv1ThenVp9PerHeight()
    {
        var info = await Resolver(new FakeRunner(Fixture("youtube-bbb.json"))).ResolveAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ", null, default);
        Assert.Equal("Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film", info.Title);
        Assert.Equal([2160, 1440, 1080, 720, 480, 360, 240, 144], info.Choices.Where(c => !c.AudioOnly).Select(c => c.Height!.Value));
        var hd = info.Choices.Single(c => c.Height == 1080);
        Assert.Equal(new VideoChoice("299+140", "1080p MP4, 255 MB", "mp4", 257619653 + 10271496, 1080, false), hd);
        Assert.Equal("401+140", info.Choices.Single(c => c.Height == 2160).Id); // no AVC at 2160: AV1 before VP9
        var sound = info.Choices.Last();
        Assert.Equal(new VideoChoice("140", "Sound only M4A, 9.8 MB", "m4a", 10271496, null, true), sound);
    }

    [Fact]
    public async Task MatchingSoundIsPairedWithEachPicture()
    {
        var json = JsonNode.Parse(Fixture("youtube-bbb.json"))!;
        // Leave 720p only in VP9: its sound has to be Opus, and the file WebM.
        var formats = json["formats"]!.AsArray();
        foreach (var f in formats.Where(f => (int?)f!["height"] == 720 && !((string)f!["vcodec"]!).StartsWith("vp")).ToList()) formats.Remove(f);
        var info = await Resolver(new FakeRunner(json.ToJsonString())).ResolveAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ", null, default);
        var choice = info.Choices.Single(c => c.Height == 720);
        Assert.Equal("302+251", choice.Id);
        Assert.Equal("webm", choice.Container);
    }

    [Fact]
    public async Task StreamsCarryTheirAddressHeadersAndChunkSize()
    {
        var resolver = Resolver(new FakeRunner(Fixture("youtube-bbb.json")));
        var resolved = await resolver.StreamsAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ", "299+140", null, default);
        var streams = resolved.Streams;
        Assert.Equal("mp4", resolved.Choice.Container);
        Assert.StartsWith("Big Buck Bunny", resolved.Info.Title);
        Assert.Equal(["299", "140"], streams.Select(s => s.FormatId));
        Assert.Equal("https://media.test/aqz-KE-bpKQ/299", streams[0].Url);
        Assert.Equal(257619653, streams[0].TotalBytes);
        Assert.Equal(10485760, streams[0].ChunkSize);
        Assert.Equal("https", streams[0].Protocol);
        Assert.StartsWith("Mozilla/5.0", streams[0].Headers!["User-Agent"]);
    }

    [Fact]
    public async Task CookiesFileExistsOnlyDuringTheRun()
    {
        string? seen = null;
        var runner = new FakeRunner(Fixture("youtube-zoo.json"), args =>
        {
            var at = args.ToList().IndexOf("--cookies");
            Assert.True(at >= 0);
            seen = File.ReadAllText(args[at + 1]);
            Assert.DoesNotContain(args, a => a.Contains("abc123"));
        });
        var headers = new RequestHeaders("https://www.youtube.com/", "SID=abc123; PREF=f6=40", "Mozilla/5.0 Test", null);
        await Resolver(runner).ResolveAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", headers, default);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc123", seen);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\tf6=40", seen);
        Assert.Empty(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
        Assert.Contains("--user-agent", runner.Args!);
    }

    [Fact]
    public async Task AStreamYtDlpCannotDescribeIsStillOffered()
    {
        // A bare HLS list, as yt-dlp reports it: no codecs, no height. It may hold picture and sound.
        const string json = """{"title": "clip", "duration": 6, "formats": [{"format_id": "0", "url": "http://127.0.0.1:9/clip.m3u8", "ext": "mp4", "protocol": "m3u8_native", "video_ext": "mp4", "audio_ext": "none"}]}""";
        var info = await Resolver(new FakeRunner(json)).ResolveAsync("http://127.0.0.1:9/clip.m3u8", null, default);
        Assert.Equal([new VideoChoice("0", "Video MP4", "mp4", null, null, false)], info.Choices);
    }

    [Fact]
    public async Task UnsupportedAddressSaysSoPlainly()
    {
        var runner = new FakeRunner("", exit: 1, stderr: "ERROR: Unsupported URL: https://shop.test/\n");
        var error = await Assert.ThrowsAsync<VideoException>(() => Resolver(runner).ResolveAsync("https://shop.test/", null, default));
        Assert.Equal("IDM-Rea found no video on this page.", error.Message);
        Assert.True(error.Unsupported);
    }

    [Fact]
    public async Task ASiteErrorIsPassedOnWithoutItsPrefix()
    {
        var runner = new FakeRunner("", exit: 1, stderr: "WARNING: x\nERROR: [youtube] jNQXAC9IVRw: Sign in to confirm your age. This video may be inappropriate for some users.\n");
        var error = await Assert.ThrowsAsync<VideoException>(() => Resolver(runner).ResolveAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", null, default));
        Assert.Equal("Sign in to confirm your age. This video may be inappropriate for some users.", error.Message);
    }

    [Fact]
    public async Task DrmIsRefusedPlainly()
    {
        var json = JsonNode.Parse(Fixture("youtube-zoo.json"))!;
        foreach (var f in json["formats"]!.AsArray()) f!["has_drm"] = true;
        var error = await Assert.ThrowsAsync<VideoException>(() => Resolver(new FakeRunner(json.ToJsonString())).ResolveAsync("https://video.test/1", null, default));
        Assert.Equal("This video is protected against copying (DRM), so it can't be downloaded.", error.Message);
    }

    [Fact]
    public async Task YtDlpRunsWithDenoAndNoShell()
    {
        var runner = new FakeRunner(Fixture("youtube-zoo.json"));
        await Resolver(runner).ResolveAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", null, default);
        Assert.Equal(@"C:\tools\yt-dlp.exe", runner.Exe);
        Assert.Equal(["-J", "--no-playlist", "--no-warnings", "--js-runtimes", @"deno:C:\tools\deno\deno.exe", "--", "https://www.youtube.com/watch?v=jNQXAC9IVRw"], runner.Args);
    }

    private sealed class FakeRunner(string stdout, Action<IReadOnlyList<string>>? during = null, int exit = 0, string stderr = "") : IToolRunner
    {
        public string? Exe { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public Task<ToolRun> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        {
            Exe = exe; Args = args; during?.Invoke(args);
            return Task.FromResult(new ToolRun(exit, stdout, stderr));
        }
    }
}
