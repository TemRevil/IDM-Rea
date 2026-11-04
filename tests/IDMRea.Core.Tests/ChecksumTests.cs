using System.Security.Cryptography;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

/// <summary>A checksum the site lists next to the link is compared with the finished file before it gets its name.</summary>
public sealed class ChecksumTests
{
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "idmrea-tests-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static async Task<DownloadJob> Wait(DownloadManager manager, Guid id, params DownloadStatus[] states)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true) { var job = manager.Snapshot().Single(j => j.Id == id); if (states.Contains(job.Status)) return job; await Task.Delay(25, timeout.Token); }
    }
    private static readonly string Wrong = new('0', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingChecksumIsMarkedVerified(bool upperCase)
    {
        await using var server = new Fixture(); var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        var sum = Convert.ToHexString(SHA256.HashData(server.Body)); if (!upperCase) sum = sum.ToLowerInvariant();
        var job = await manager.AddAsync(server.Url, dir, expectedSha256: sum);
        var done = await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.True(done.Verified);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
    }

    [Fact]
    public async Task NoChecksumMeansNotVerified()
    {
        await using var server = new Fixture(); var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        var done = await Wait(manager, (await manager.AddAsync(server.Url, dir)).Id, DownloadStatus.Completed, DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.False(done.Verified);
    }

    [Fact]
    public async Task WrongChecksumFailsWithAPlainReasonAndNeverTakesTheName()
    {
        await using var server = new Fixture(); var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        var done = await Wait(manager, (await manager.AddAsync(server.Url, dir, expectedSha256: Wrong)).Id, DownloadStatus.Completed, DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.Equal(ErrorKind.ChecksumMismatch, done.ErrorKind);
        Assert.Equal("The file doesn't match the checksum on the site. Retry to download it again.", done.Error);
        Assert.False(File.Exists(done.Destination));
    }

    [Fact]
    public async Task RetryAfterAMismatchDownloadsAgain()
    {
        await using var server = new Fixture(); var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        var id = (await manager.AddAsync(server.Url, dir, expectedSha256: Wrong)).Id;
        await Wait(manager, id, DownloadStatus.Failed);
        var before = server.RequestCount;
        await manager.RetryAsync(id);
        await Wait(manager, id, DownloadStatus.Failed);
        Assert.True(server.RequestCount > before, "the retry must fetch the file again, not re-check the same bytes");
    }

    [Fact]
    public async Task AChecksumMustBe64HexCharacters()
    {
        await using var server = new Fixture(); var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddAsync(server.Url, dir, expectedSha256: "abc"));
    }
}
