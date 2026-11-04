using System.Text.Json;
using IDMRea.Contracts;
using Xunit;
namespace IDMRea.Tests;

public sealed class ContractsTests
{
    [Fact]
    public void BrowserSettingsDefaultAndClamp()
    {
        var settings = new EngineSettings();
        Assert.True(settings.BrowserTakeover);
        Assert.Equal(1024 * 1024, settings.BrowserMinBytes);
        Assert.Equal(0, (settings with { BrowserMinBytes = -5 }).Clamped().BrowserMinBytes);
        Assert.Equal(1024L * 1024 * 1024, (settings with { BrowserMinBytes = long.MaxValue }).Clamped().BrowserMinBytes);
        var json = JsonSerializer.Serialize(settings with { BrowserTakeover = false }, Protocol.Json);
        Assert.False(JsonSerializer.Deserialize<EngineSettings>(json, Protocol.Json)!.BrowserTakeover);
        Assert.True(JsonSerializer.Deserialize<EngineSettings>("{}", Protocol.Json)!.BrowserTakeover);
    }

    [Fact]
    public void EachDataFolderHasItsOwnAppWindow()
    {
        // A second copy run on another data folder (a test run, say) must not hand its launch to the first window.
        var mine = Protocol.InstanceKeyFor(Path.Combine(Path.GetTempPath(), "a"));
        Assert.Equal(mine, Protocol.InstanceKeyFor(Path.Combine(Path.GetTempPath(), "a")));
        Assert.NotEqual(mine, Protocol.InstanceKeyFor(Path.Combine(Path.GetTempPath(), "b")));
        Assert.NotEqual(Protocol.PipeName, Protocol.InstanceKey);
    }

    [Fact]
    public void SettingsClampAndUltraValues()
    {
        var clamped = new EngineSettings(DownloadsAtOnce: 40, PartsPerFile: 0, RetryAttempts: -1, SpeedLimitBytesPerSecond: -5).Clamped();
        Assert.Equal(10, clamped.DownloadsAtOnce); Assert.Equal(1, clamped.PartsPerFile); Assert.Equal(0, clamped.RetryAttempts); Assert.Equal(0, clamped.SpeedLimitBytesPerSecond);
        var ultra = new EngineSettings(PartsPerFile: 4, UltraPerformance: true);
        Assert.Equal(16, ultra.EffectiveParts); Assert.Equal(1048576, ultra.BufferSize); Assert.Equal(524288, ultra.MinPartSize);
        var normal = new EngineSettings();
        Assert.Equal(8, normal.EffectiveParts); Assert.Equal(262144, normal.BufferSize); Assert.Equal(1048576, normal.MinPartSize);
        Assert.Equal(3, normal.DownloadsAtOnce); Assert.Equal(5, normal.RetryAttempts);
    }

    [Fact]
    public void CategoryFoldersDefaultUnderDownloads()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Assert.Equal(Path.Combine(downloads, "Video"), new EngineSettings().FolderFor(Category.Video));
        Assert.Equal(downloads, new EngineSettings().FolderFor(Category.Other));
        var custom = new EngineSettings(Folders: new Dictionary<Category, string> { [Category.Music] = "/tmp/music" });
        Assert.Equal("/tmp/music", custom.FolderFor(Category.Music));
        Assert.Equal(Path.Combine(downloads, "Archives"), custom.FolderFor(Category.Archives));
    }

    [Theory]
    [InlineData("a.MKV", null, Category.Video)]
    [InlineData("b.tar.gz", null, Category.Archives)]
    [InlineData("setup.exe", null, Category.Programs)]
    [InlineData("song.flac", null, Category.Music)]
    [InlineData("report.pdf", null, Category.Documents)]
    [InlineData("photo.webp", null, Category.Images)]
    [InlineData("x", "audio/mpeg", Category.Music)]
    [InlineData("x", "video/mp4", Category.Video)]
    [InlineData("x", "application/pdf", Category.Documents)]
    [InlineData("x", null, Category.Other)]
    [InlineData("data.unknownext", null, Category.Other)]
    public void CategoryFromNameAndType(string name, string? type, Category expected) => Assert.Equal(expected, Categories.From(name, type));

    [Theory]
    [InlineData("application/zip", ".zip")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData(null, "")]
    [InlineData("application/octet-stream", "")]
    public void ExtensionForContentType(string? type, string expected) => Assert.Equal(expected, Categories.ExtensionFor(type));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(13002342L, "12.4 MB")]
    [InlineData(671088640L, "640 MB")]
    [InlineData(1610612736L, "1.5 GB")]
    public void SizeText(long bytes, string expected) => Assert.Equal(expected, Text.Size(bytes));

    [Fact]
    public void SpeedAndTimeText()
    {
        Assert.Equal("8.2 MB/s", Text.Speed(8598323));
        Assert.Equal("45 s left", Text.TimeLeft(TimeSpan.FromSeconds(45)));
        Assert.Equal("59 s left", Text.TimeLeft(TimeSpan.FromSeconds(59)));
        Assert.Equal("1 min left", Text.TimeLeft(TimeSpan.FromSeconds(60)));
        Assert.Equal("2 min left", Text.TimeLeft(TimeSpan.FromSeconds(61)));
        Assert.Equal("1 h 5 min left", Text.TimeLeft(TimeSpan.FromMinutes(65)));
    }

    private static DownloadJob Job(DownloadStatus status, long received = 13002342, long? total = 671088640) =>
        new(Guid.NewGuid(), "https://example.com/f.bin", "/d/f.bin", "/d/f.bin.part", status, received, total);

    [Fact]
    public void StatusLineCopy()
    {
        var now = DateTimeOffset.UtcNow;
        // 640 MB - 12.4 MB = 627.6 MB left at 8.2 MB/s = 76.5 s → rounds up to 2 min.
        Assert.Equal("12.4 MB of 640 MB - 8.2 MB/s - 2 min left", Text.StatusLine(Job(DownloadStatus.Downloading), 8598323, now));
        Assert.Equal("12.4 MB - 8.2 MB/s", Text.StatusLine(Job(DownloadStatus.Downloading, total: null), 8598323, now));
        Assert.Equal("12.4 MB of 640 MB", Text.StatusLine(Job(DownloadStatus.Downloading), 0, now));
        Assert.Equal("Waiting", Text.StatusLine(Job(DownloadStatus.Queued), 0, now));
        Assert.Equal("Paused - 12.4 MB of 640 MB", Text.StatusLine(Job(DownloadStatus.Paused), 0, now));
        Assert.Equal("Retrying in 12 s (attempt 2 of 5)", Text.StatusLine(Job(DownloadStatus.Retrying) with { Attempts = 2, NextRetryAt = now.AddSeconds(12) }, 0, now, retryAttempts: 5));
        Assert.Equal("Finishing", Text.StatusLine(Job(DownloadStatus.Finalizing), 0, now));
        Assert.Equal("Done - 640 MB", Text.StatusLine(Job(DownloadStatus.Completed, 671088640), 0, now));
        Assert.Equal("Failed - the server refused the request.", Text.StatusLine(Job(DownloadStatus.Failed) with { ErrorKind = ErrorKind.ClientError, HttpStatus = 403 }, 0, now));
        Assert.Equal("Cancelled", Text.StatusLine(Job(DownloadStatus.Cancelled), 0, now));
    }

    [Theory]
    [InlineData(ErrorKind.Network, null, "Can't reach the server. Check your connection.")]
    [InlineData(ErrorKind.Timeout, null, "The server stopped responding.")]
    [InlineData(ErrorKind.Server, 503, "The server had a problem (HTTP 503).")]
    [InlineData(ErrorKind.RateLimited, 429, "The server asked to slow down.")]
    [InlineData(ErrorKind.ClientError, 404, "The server no longer has this file. Check the link.")]
    [InlineData(ErrorKind.ClientError, 410, "The server no longer has this file. Check the link.")]
    [InlineData(ErrorKind.ClientError, 401, "The server refused the request.")]
    [InlineData(ErrorKind.ClientError, 418, "The server refused the request (HTTP 418).")]
    [InlineData(ErrorKind.Changed, null, "The file changed on the server. Retry to download it again from the start.")]
    [InlineData(ErrorKind.DiskFull, null, "Not enough disk space.")]
    [InlineData(ErrorKind.AccessDenied, null, "Can't write to the save folder.")]
    [InlineData(ErrorKind.Other, null, "Something went wrong.")]
    public void ErrorWording(ErrorKind kind, int? status, string expected) => Assert.Equal(expected, Text.Describe(kind, status));

    [Fact]
    public void NoEmDashesInText()
    {
        foreach (var kind in Enum.GetValues<ErrorKind>()) Assert.DoesNotContain('—', Text.Describe(kind, 500));
        foreach (var status in Enum.GetValues<DownloadStatus>()) Assert.DoesNotContain('—', Text.StatusLine(Job(status), 1000, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("١٢٣", "123")]
    [InlineData("۴۵", "45")]
    [InlineData("7a٠", "7a0")]
    public void DigitsNormalised(string input, string expected) => Assert.Equal(expected, Text.NormalizeDigits(input));

    [Fact]
    public void NumbersUseAsciiDigitsUnderArabicCulture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try { Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA"); Assert.Equal("12.4 MB", Text.Size(13002342)); }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void V1PayloadStillReads()
    {
        const string v1 = "{\"id\":\"6f1c2e1a-2b3c-4d5e-8f90-123456789abc\",\"url\":\"https://e.com/f.bin\",\"destination\":\"/d/f.bin\",\"partialPath\":\"/d/f.bin.x.part\",\"status\":\"Completed\",\"receivedBytes\":5,\"totalBytes\":5,\"eTag\":\"\\\"v1\\\"\",\"lastModified\":null,\"error\":null,\"updatedAt\":\"2026-11-03T10:00:00+00:00\",\"completionSha256\":\"AB\"}";
        var job = JsonSerializer.Deserialize<DownloadJob>(v1, Protocol.Json)!;
        Assert.Equal(DownloadStatus.Completed, job.Status); Assert.Null(job.Parts); Assert.Equal(Category.Other, job.Category); Assert.Equal(PausedBy.None, job.PausedBy);
        Assert.Equal("f.bin", job.FileName);
    }

    [Fact]
    public void UniquePathAddsCounter()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("/d", "a.zip"), Path.Combine("/d", "a (1).zip") };
        Assert.Equal(Path.Combine("/d", "a (2).zip"), FileNames.UniquePath("/d", "a.zip", taken.Contains));
        Assert.Equal(Path.Combine("/d", "b.zip"), FileNames.UniquePath("/d", "b.zip", taken.Contains));
        Assert.Equal("_CON.txt", FileNames.SafeName("CON.txt"));
    }
}
