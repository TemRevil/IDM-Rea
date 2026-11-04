using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

/// <summary>Video jobs: the streams a page plays are fetched by the engine, then joined into one file.</summary>
public sealed class VideoJobTests : IDisposable
{
    private readonly string data = Path.Combine(Path.GetTempPath(), "idmrea-videojob-" + Guid.NewGuid());
    private string Folder => Path.Combine(data, "Videos");
    public VideoJobTests() => Directory.CreateDirectory(data);
    public void Dispose() { try { Directory.Delete(data, true); } catch (IOException) { } }

    private static readonly byte[] Picture = RandomNumberGenerator.GetBytes(512 * 1024);
    private static readonly byte[] Sound = RandomNumberGenerator.GetBytes(96 * 1024);
    private const string Page = "https://www.video.test/watch?v=abc";

    private static async Task<DownloadJob> Until(DownloadManager manager, Guid id, Func<DownloadJob, bool> done, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { var job = manager.Snapshot().Single(j => j.Id == id); if (done(job)) return job; await Task.Delay(20, timeout.Token); }
    }

    private DownloadManager Manager(Web web, Streams streams, Joiner joiner) =>
        new(data, new ManagerOptions(Handler: web, Video: new VideoServices(streams, joiner)));

    [Fact]
    public async Task AVideoJobDownloadsBothStreamsAndJoinsThem()
    {
        var web = new Web(); web.Files["/v1"] = Picture; web.Files["/a1"] = Sound;
        var streams = new Streams(); var joiner = new Joiner();
        await using var manager = Manager(web, streams, joiner);
        var job = await manager.AddVideoAsync(Page, "299+140", Folder);
        Assert.Equal(Path.Combine(Folder, "Big Buck Bunny (1080p).mp4"), job.Destination);
        Assert.Equal(Category.Video, job.Category);
        job = await Until(manager, job.Id, j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Equal([.. Picture, .. Sound], File.ReadAllBytes(job.Destination));
        Assert.Equal([Picture, Sound], joiner.Seen[0]); // each stream whole, picture first
        Assert.Equal([job.Destination], Directory.EnumerateFiles(Folder)); // no stream parts left behind
        Assert.All(web.Spans, span => Assert.True(span <= 64 * 1024, $"a request asked for {span} bytes"));
        Assert.All(web.Agents, agent => Assert.Equal("Mozilla/5.0 Stream", agent)); // the stream's own headers are sent
        Assert.All(web.Languages, language => Assert.Equal("en-us,en;q=0.5", language));
        Assert.Equal(0, web.Cookies); // but never a cookie it names
    }

    [Fact]
    public async Task AnExpiredAddressIsResolvedAgainAndContinues()
    {
        var web = new Web { ExpireAfter = ("/v1", 3) };
        web.Files["/v1"] = Picture; web.Files["/v2"] = Picture; web.Files["/a1"] = Sound; web.Files["/a2"] = Sound;
        var streams = new Streams();
        await using var manager = Manager(web, streams, new Joiner());
        var job = await manager.AddVideoAsync(Page, "299+140", Folder);
        job = await Until(manager, job.Id, j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Equal([.. Picture, .. Sound], File.ReadAllBytes(job.Destination));
        Assert.Equal(2, streams.Calls); // once to add, once when the address stopped working
        Assert.Equal(Picture.Length, web.Served("/v1") + web.Served("/v2")); // carried on, nothing fetched twice
        Assert.True(web.Served("/v2") > 0);
    }

    [Fact]
    public async Task StopDuringJoiningJoinsAgainOnResume()
    {
        var web = new Web(); web.Files["/v1"] = Picture; web.Files["/a1"] = Sound;
        var streams = new Streams(); var joiner = new Joiner { HangFirst = true };
        Guid id;
        await using (var manager = Manager(web, streams, joiner))
        {
            id = (await manager.AddVideoAsync(Page, "299+140", Folder)).Id;
            var joining = await Until(manager, id, j => j.Video?.Stage == VideoStage.Joining && joiner.Calls == 1);
            Assert.All(joining.Video!.Streams, s => Assert.True(s.Done));
        }
        var requests = web.Requests;
        await using (var manager = Manager(web, streams, joiner))
        {
            var job = await Until(manager, id, j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed);
            Assert.Equal(DownloadStatus.Completed, job.Status);
            Assert.Equal([.. Picture, .. Sound], File.ReadAllBytes(job.Destination));
        }
        Assert.Equal(2, joiner.Calls);
        Assert.Equal(requests, web.Requests); // the streams were not fetched again
        Assert.Equal(1, streams.Calls);
    }

    [Fact]
    public async Task RemovingAVideoJobDeletesItsStreamParts()
    {
        var web = new Web(); web.Files["/v1"] = Picture; web.Files["/a1"] = Sound;
        var joiner = new Joiner { HangFirst = true };
        await using var manager = Manager(web, new Streams(), joiner);
        var job = await manager.AddVideoAsync(Page, "299+140", Folder);
        await Until(manager, job.Id, j => j.Video?.Stage == VideoStage.Joining && joiner.Calls == 1);
        Assert.Equal(3, Directory.EnumerateFiles(Folder, "*.part").Count()); // two streams and the join under way
        await manager.RemoveAsync(job.Id, false);
        Assert.Empty(Directory.EnumerateFiles(Folder));
    }

    /// <summary>yt-dlp's answer: picture /v{n} and sound /a{n}, where n counts the calls.</summary>
    private sealed class Streams : IVideoStreams
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<ResolvedStreams> StreamsAsync(string pageUrl, string choiceId, RequestHeaders? headers, CancellationToken token)
        {
            var n = Interlocked.Increment(ref calls);
            var choice = new VideoChoice("299+140", "1080p MP4, 600 KB", "mp4", Picture.Length + Sound.Length, 1080, false);
            var info = new VideoInfo(pageUrl, "Big Buck Bunny", 60, null, [choice]);
            var agent = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 Stream", ["Accept-Language"] = "en-us,en;q=0.5", ["Cookie"] = "leak=1" };
            return Task.FromResult(new ResolvedStreams(info, choice, [
                new VideoStream("299", $"https://media.test/v{n}", "https", "mp4", agent, Picture.Length, 64 * 1024),
                new VideoStream("140", $"https://media.test/a{n}", "https", "m4a", agent, Sound.Length, 64 * 1024),
            ]));
        }
    }

    /// <summary>Joins by putting the files one after the other. With <see cref="HangFirst"/> the first join writes a
    /// little and then waits until it is stopped.</summary>
    private sealed class Joiner : IVideoJoiner
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool HangFirst { get; init; }
        public List<byte[][]> Seen { get; } = [];
        public async Task JoinAsync(IReadOnlyList<string> inputs, string output, string container, CancellationToken token)
        {
            var n = Interlocked.Increment(ref calls);
            var parts = inputs.Select(File.ReadAllBytes).ToArray();
            lock (Seen) Seen.Add(parts);
            if (HangFirst && n == 1)
            {
                await File.WriteAllBytesAsync(output, [1, 2, 3], token);
                await Task.Delay(Timeout.Infinite, token);
            }
            await File.WriteAllBytesAsync(output, parts.SelectMany(p => p).ToArray(), token);
        }
    }

    /// <summary>Serves byte ranges of <see cref="Files"/> by path. <see cref="ExpireAfter"/> answers 403 for a path
    /// once it has served that many requests.</summary>
    private sealed class Web : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public (string Path, int Requests)? ExpireAfter { get; init; }
        private readonly Dictionary<string, long> served = [];
        private readonly Dictionary<string, int> hits = [];
        public List<long> Spans { get; } = [];
        public List<string> Agents { get; } = [];
        public List<string> Languages { get; } = [];
        public int Cookies { get; private set; }
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public long Served(string path) { lock (served) return served.GetValueOrDefault(path); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            var path = request.RequestUri!.AbsolutePath;
            lock (served)
            {
                hits[path] = hits.GetValueOrDefault(path) + 1;
                Agents.Add(string.Join(" ", request.Headers.UserAgent.Select(u => u.ToString())));
                Languages.Add(request.Headers.NonValidated["Accept-Language"].ToString());
                if (request.Headers.Contains("Cookie")) Cookies++;
                if (ExpireAfter is var (expired, after) && path == expired && hits[path] > after) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            if (!Files.TryGetValue(path, out var file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var range = request.Headers.Range?.Ranges.Single();
            long from = range?.From ?? 0, to = Math.Min(range?.To ?? file.Length - 1, file.Length - 1);
            var slice = file.AsSpan((int)from, (int)(to - from + 1)).ToArray();
            lock (served) { served[path] = served.GetValueOrDefault(path) + slice.Length; Spans.Add(slice.Length); }
            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
            response.Headers.AcceptRanges.Add("bytes");
            if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
            return Task.FromResult(response);
        }
    }
}
