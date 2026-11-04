using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

/// <summary>Video streams: requests capped at the stream's chunk size, and files whose size alone identifies them.</summary>
public sealed class CappedTransferTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "idmrea-capped-" + Guid.NewGuid());
    public CappedTransferTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);

    private static TransferContext Context(HttpClient http) =>
        new(http, new RateLimiter(), new SpeedMeter(), () => new EngineSettings(), _ => { });

    private DownloadJob Job(Fixture server) =>
        new(Guid.NewGuid(), server.Url, Path.Combine(dir, "video.mp4"), Path.Combine(dir, "video.mp4.part"), DownloadStatus.Downloading);

    [Fact]
    public async Task NoRequestIsLargerThanTheCap()
    {
        await using var server = new Fixture { Mode = "last-modified", BodySize = 1024 * 1024 };
        using var http = new HttpClient();
        var probe = new ProbeResult(server.Url, server.Url, "video.mp4", server.Body.Length, null, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null,
            Category.Video, true, MaxRequestBytes: 64 * 1024);
        var job = await MultiPartTransfer.RunAsync(Job(server), probe, Context(http), default);
        Assert.Equal(server.Body, File.ReadAllBytes(job.PartialPath));
        var spans = server.Ranges.Select(r => r!.Split('=')[1].Split('-')).Select(s => long.Parse(s[1]) - long.Parse(s[0]) + 1).ToList();
        Assert.True(spans.Count >= 16, $"{spans.Count} requests");
        Assert.All(spans, span => Assert.True(span <= 64 * 1024, $"a request asked for {span} bytes"));
    }

    [Fact]
    public async Task AFixedFileResumesWithoutValidators()
    {
        await using var server = new Fixture { Mode = "no-validator", BodySize = 512 * 1024 };
        using var http = new HttpClient();
        var half = server.Body.Length / 2;
        var job = Job(server);
        File.WriteAllBytes(job.PartialPath, [.. server.Body.AsSpan(0, half), .. new byte[server.Body.Length - half]]);
        job = job with
        {
            TotalBytes = server.Body.Length, ReceivedBytes = half, Resumable = true,
            Parts = [new DownloadPart(0, half - 1, half), new DownloadPart(half, server.Body.Length - 1, 0)],
        };
        var probe = new ProbeResult(server.Url, server.Url, "video.mp4", server.Body.Length, null, null, null, Category.Video, true, FixedFile: true);
        job = await MultiPartTransfer.RunAsync(job, probe, Context(http), default);
        Assert.Equal(server.Body, File.ReadAllBytes(job.PartialPath));
        Assert.All(server.Ranges, r => Assert.StartsWith($"bytes={half}", r)); // only the missing half was asked for
        Assert.All(server.RequestHeaders, h => Assert.False(h.ContainsKey("If-Range")));
    }
}
