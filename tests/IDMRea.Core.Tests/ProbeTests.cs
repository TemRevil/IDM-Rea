using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class ProbeTests
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task ProbeReadsSizeRangesAndValidator()
    {
        await using var server = new Fixture { BodySize = 3 * 1024 * 1024, Path = "/files/movie.mkv" };
        var probe = await Prober.ProbeAsync(Http, server.Url, default);
        Assert.Equal(3 * 1024 * 1024, probe.TotalBytes); Assert.True(probe.AcceptsRanges); Assert.True(probe.Resumable);
        Assert.Equal("\"v1\"", probe.ETag); Assert.Equal("movie.mkv", probe.FileName); Assert.Equal(Category.Video, probe.Category);
        Assert.Equal("bytes=0-0", Assert.Single(server.Ranges));
    }

    [Fact]
    public async Task ProbeWithoutRanges()
    {
        await using var server = new Fixture { Mode = "ignore" };
        var probe = await Prober.ProbeAsync(Http, server.Url, default);
        Assert.False(probe.AcceptsRanges); Assert.False(probe.Resumable); Assert.Equal(server.Body.Length, probe.TotalBytes);
    }

    [Fact]
    public async Task WeakOrMissingValidatorIsNotResumable()
    {
        await using var server = new Fixture { Mode = "no-validator" };
        Assert.False((await Prober.ProbeAsync(Http, server.Url, default)).Resumable);
        await using var dated = new Fixture { Mode = "last-modified" };
        var probe = await Prober.ProbeAsync(Http, dated.Url, default);
        Assert.True(probe.Resumable); Assert.NotNull(probe.LastModified);
    }

    [Theory]
    [InlineData("attachment; filename=\"r.pdf\"", "r.pdf")]
    [InlineData("attachment; filename=plain.txt", "plain.txt")]
    [InlineData("attachment; filename*=UTF-8''%D9%85%D9%84%D9%81.zip", "ملف.zip")]
    [InlineData("attachment; filename=\"fallback.zip\"; filename*=UTF-8''real%20name.zip", "real name.zip")]
    [InlineData("inline", null)]
    [InlineData(null, null)]
    public void FileNameFromHeaders(string? header, string? expected) => Assert.Equal(expected, Prober.FileNameFrom(header));

    [Fact]
    public async Task ContentDispositionNameWins()
    {
        await using var server = new Fixture { Path = "/dl/12345" };
        server.Headers["Content-Disposition"] = "attachment; filename=\"Quarterly report.pdf\"";
        var probe = await Prober.ProbeAsync(Http, server.Url, default);
        Assert.Equal("Quarterly report.pdf", probe.FileName); Assert.Equal(Category.Documents, probe.Category);
    }

    [Fact]
    public async Task NoNameInUrl()
    {
        await using var server = new Fixture { Path = "/get?id=5" };
        server.Headers["Content-Type"] = "application/zip";
        var probe = await Prober.ProbeAsync(Http, server.Url, default);
        Assert.Equal("download.zip", probe.FileName); Assert.Equal(Category.Archives, probe.Category);
        await using var bare = new Fixture { Path = "/" };
        Assert.Equal("download", (await Prober.ProbeAsync(Http, bare.Url, default)).FileName);
    }

    [Fact]
    public async Task UnsafeNamesAreCleaned()
    {
        await using var server = new Fixture { Path = "/x" };
        server.Headers["Content-Disposition"] = "attachment; filename=\"..\\\\evil:name?.exe\"";
        var name = (await Prober.ProbeAsync(Http, server.Url, default)).FileName;
        Assert.DoesNotContain('\\', name); Assert.DoesNotContain(':', name); Assert.DoesNotContain('?', name); Assert.EndsWith(".exe", name);
    }

    [Fact]
    public async Task RedirectGivesResolvedUrl()
    {
        await using var server = new Fixture { RedirectOnce = true };
        var probe = await Prober.ProbeAsync(Http, server.Url, default);
        Assert.Equal(server.Url, probe.Url); Assert.EndsWith("/moved/file.bin", probe.ResolvedUrl);
        Assert.Equal(server.Body.Length, probe.TotalBytes);
    }

    [Theory]
    [InlineData(404, ErrorKind.ClientError)]
    [InlineData(403, ErrorKind.ClientError)]
    [InlineData(503, ErrorKind.Server)]
    [InlineData(429, ErrorKind.RateLimited)]
    [InlineData(408, ErrorKind.Timeout)]
    public async Task ErrorStatusesThrowTypedErrors(int status, ErrorKind kind)
    {
        await using var server = new Fixture { Status = status };
        if (status == 429) server.Headers["Retry-After"] = "7";
        var ex = await Assert.ThrowsAsync<TransferException>(() => Prober.ProbeAsync(Http, server.Url, default));
        Assert.Equal(kind, ex.Kind); Assert.Equal(status, ex.HttpStatus);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }
}
