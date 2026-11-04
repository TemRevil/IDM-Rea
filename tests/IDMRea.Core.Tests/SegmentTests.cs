using System.Net;
using System.Security.Cryptography;
using System.Text;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class SegmentTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "idmrea-segments-" + Guid.NewGuid());
    public SegmentTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);
    private static readonly Uri Base = new("https://cdn.test/video/master.m3u8");

    [Fact]
    public void MasterPlaylistListsItsVariants()
    {
        var variants = Hls.ParseMaster("""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            360/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            https://other.test/1080.m3u8
            """, Base);
        Assert.Equal([new HlsVariant(new Uri("https://cdn.test/video/360/index.m3u8"), 800000, 360), new HlsVariant(new Uri("https://other.test/1080.m3u8"), 5000000, 1080)], variants);
        Assert.True(Hls.IsMaster("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\na.m3u8"));
        Assert.False(Hls.IsMaster("#EXTM3U\n#EXTINF:4,\na.ts"));
    }

    [Fact]
    public void MediaPlaylistSegmentsWithByteRangesAndInit()
    {
        var list = Hls.ParseMedia("""
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXTINF:4.0,
            #EXT-X-BYTERANGE:1000@720
            media.mp4
            #EXTINF:4.0,
            #EXT-X-BYTERANGE:500
            media.mp4
            #EXTINF:2.5,
            https://cdn2.test/last.m4s
            #EXT-X-ENDLIST
            """, Base);
        Assert.Equal([
            new HlsSegment(new Uri("https://cdn.test/video/init.mp4"), 0, 720, null, 0),
            new HlsSegment(new Uri("https://cdn.test/video/media.mp4"), 720, 1000, null, 7),
            new HlsSegment(new Uri("https://cdn.test/video/media.mp4"), 1720, 500, null, 8),
            new HlsSegment(new Uri("https://cdn2.test/last.m4s"), null, null, null, 9),
        ], list);
    }

    [Fact]
    public void ALiveStreamOrSampleEncryptionIsRefused()
    {
        var live = Assert.Throws<VideoException>(() => Hls.ParseMedia("#EXTM3U\n#EXTINF:4,\na.ts\n", Base));
        Assert.Equal("This is a live stream. IDM-Rea downloads videos that have finished.", live.Message);
        var drm = Assert.Throws<VideoException>(() => Hls.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", Base));
        Assert.Equal("This video is protected against copying (DRM), so it can't be downloaded.", drm.Message);
    }

    [Fact]
    public async Task Aes128SegmentsAreDecrypted()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var plain = new[] { RandomNumberGenerator.GetBytes(1000), RandomNumberGenerator.GetBytes(777) };
        var web = new Web();
        web.Files["https://cdn.test/video/key.bin"] = key;
        web.Files["https://cdn.test/video/a.ts"] = Encrypt(plain[0], key, Iv(3));
        var explicitIv = RandomNumberGenerator.GetBytes(16);
        web.Files["https://cdn.test/video/b.ts"] = Encrypt(plain[1], key, explicitIv);
        var segments = Hls.ParseMedia($"""
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:3
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXTINF:4,
            a.ts
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x{Convert.ToHexString(explicitIv)}
            #EXTINF:4,
            b.ts
            #EXT-X-ENDLIST
            """, Base);
        var output = Path.Combine(dir, "out.ts");
        var result = await SegmentTransfer.RunAsync(segments, output, new SegmentProgress(0, 0), Context(web), _ => { }, default);
        Assert.Equal([.. plain[0], .. plain[1]], File.ReadAllBytes(output));
        Assert.Equal(new SegmentProgress(2, 1777), result);
        Assert.Equal(1, web.Count("https://cdn.test/video/key.bin")); // the key is fetched once
    }

    [Fact]
    public async Task SegmentsDownloadInParallelAndResumeFromTheLastCheckpoint()
    {
        var web = new Web { Delay = 60 };
        var parts = Enumerable.Range(0, 8).Select(i => Encoding.ASCII.GetBytes($"segment-{i};")).ToList();
        for (var i = 0; i < parts.Count; i++) web.Files[$"https://cdn.test/video/s{i}.ts"] = parts[i];
        var segments = Enumerable.Range(0, 8).Select(i => new HlsSegment(new Uri($"https://cdn.test/video/s{i}.ts"), null, null, null, i)).ToList();
        var output = Path.Combine(dir, "out.ts");
        // Saved state: two segments done. The file also holds a half-written third, longer than the rest, which must be cut off.
        File.WriteAllBytes(output, [.. parts[0], .. parts[1], .. Enumerable.Repeat((byte)'x', 500)]);
        var saved = new SegmentProgress(2, parts[0].Length + parts[1].Length);
        var checkpoints = new List<SegmentProgress>();
        var result = await SegmentTransfer.RunAsync(segments, output, saved, Context(web), p => { lock (checkpoints) checkpoints.Add(p); }, default);
        Assert.Equal(parts.SelectMany(p => p).ToArray(), File.ReadAllBytes(output));
        Assert.Equal(0, web.Count("https://cdn.test/video/s0.ts"));
        Assert.Equal(0, web.Count("https://cdn.test/video/s1.ts"));
        Assert.True(web.MaxConcurrent >= 2, $"at most {web.MaxConcurrent} at once");
        Assert.Equal(8, result.Done);
        Assert.Equal(Enumerable.Range(3, 6), checkpoints.Select(c => c.Done)); // one checkpoint per segment, in order
    }

    private static TransferContext Context(Web web) => new(new HttpClient(web), new RateLimiter(), new SpeedMeter(), () => new EngineSettings(), _ => { });

    private static byte[] Iv(long sequence) { var iv = new byte[16]; for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i)); return iv; }

    private static byte[] Encrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
    }

    private sealed class Web : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public int Delay { get; init; }
        private readonly Dictionary<string, int> counts = [];
        private int current, max;
        public int MaxConcurrent => Volatile.Read(ref max);
        public int Count(string url) { lock (counts) return counts.GetValueOrDefault(url); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var now = Interlocked.Increment(ref current);
            int seen; do { seen = max; } while (now > seen && Interlocked.CompareExchange(ref max, now, seen) != seen);
            try
            {
                var url = request.RequestUri!.AbsoluteUri;
                lock (counts) counts[url] = counts.GetValueOrDefault(url) + 1;
                if (Delay > 0) await Task.Delay(Delay, token);
                return Files.TryGetValue(url, out var data)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally { Interlocked.Decrement(ref current); }
        }
    }
}
