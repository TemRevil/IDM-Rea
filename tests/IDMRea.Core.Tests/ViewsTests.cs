using IDMRea.Contracts;
using Xunit;
namespace IDMRea.Tests;

public sealed class ViewsTests
{
    private static DownloadJob Job(string name, DownloadStatus status, Category category = Category.Other, long? total = 100, int minutesAgo = 0, string url = "https://example.test/x") =>
        new(Guid.NewGuid(), url, Path.Combine("C:", "d", name), Path.Combine("C:", "d", name + ".part"), status, TotalBytes: total, Category: category,
            CreatedAt: DateTimeOffset.UnixEpoch.AddHours(1).AddMinutes(-minutesAgo));

    [Theory]
    [InlineData(DownloadStatus.Downloading, "downloading")]
    [InlineData(DownloadStatus.Retrying, "downloading")]
    [InlineData(DownloadStatus.Finalizing, "downloading")]
    [InlineData(DownloadStatus.Queued, "queued")]
    [InlineData(DownloadStatus.Paused, "queued")]
    [InlineData(DownloadStatus.Completed, "completed")]
    [InlineData(DownloadStatus.Failed, "failed")]
    public void StatusFilters(DownloadStatus status, string filter)
    {
        var job = Job("a.bin", status);
        Assert.True(DownloadViews.Matches(job, filter, ""));
        Assert.True(DownloadViews.Matches(job, "all", ""));
        foreach (var other in new[] { "downloading", "queued", "completed", "failed" }.Where(f => f != filter))
            Assert.False(DownloadViews.Matches(job, other, ""));
    }

    [Fact]
    public void CancelledOnlyUnderAll()
    {
        var job = Job("a.bin", DownloadStatus.Cancelled);
        Assert.True(DownloadViews.Matches(job, "all", ""));
        Assert.Equal(["all"], DownloadViews.FilterNames.Where(f => DownloadViews.Matches(job, f, "")).Where(f => !Enum.TryParse<Category>(f, out _)));
    }

    [Fact]
    public void CategoryFilterAndSearch()
    {
        var video = Job("Holiday Clip.mp4", DownloadStatus.Completed, Category.Video, url: "https://cdn.example.test/v/123");
        Assert.True(DownloadViews.Matches(video, "Video", ""));
        Assert.False(DownloadViews.Matches(video, "Music", ""));
        Assert.True(DownloadViews.Matches(video, "all", "holiday"));
        Assert.True(DownloadViews.Matches(video, "all", "CDN.EXAMPLE"));
        Assert.False(DownloadViews.Matches(video, "all", "report"));
        Assert.True(DownloadViews.Matches(video, "Video", "  "));
        // Typed on an Arabic keyboard: Arabic-Indic and Extended Arabic-Indic digits search as 0-9.
        Assert.True(DownloadViews.Matches(video, "all", "v/١٢٣"));
        Assert.True(DownloadViews.Matches(video, "all", "v/۱۲۳"));
    }

    [Fact]
    public void Counts()
    {
        var jobs = new[]
        {
            Job("a.mp4", DownloadStatus.Downloading, Category.Video), Job("b.mp4", DownloadStatus.Completed, Category.Video),
            Job("c.pdf", DownloadStatus.Failed, Category.Documents), Job("d.zip", DownloadStatus.Paused, Category.Archives),
            Job("e.zip", DownloadStatus.Cancelled, Category.Archives),
        };
        var counts = DownloadViews.Counts(jobs);
        Assert.Equal(5, counts["all"]);
        Assert.Equal(1, counts["downloading"]);
        Assert.Equal(1, counts["queued"]);
        Assert.Equal(1, counts["completed"]);
        Assert.Equal(1, counts["failed"]);
        Assert.Equal(2, counts["Video"]);
        Assert.Equal(2, counts["Archives"]);
        Assert.Equal(0, counts["Music"]);
    }

    [Fact]
    public void Sorting()
    {
        var old = Job("b.bin", DownloadStatus.Completed, total: 300, minutesAgo: 30);
        var mid = Job("A.bin", DownloadStatus.Failed, total: null, minutesAgo: 20);
        var fresh = Job("c.bin", DownloadStatus.Downloading, total: 50, minutesAgo: 10);
        var jobs = new[] { old, mid, fresh };
        Assert.Equal([fresh, mid, old], DownloadViews.Sort(jobs, SortMode.Newest));
        Assert.Equal([mid, old, fresh], DownloadViews.Sort(jobs, SortMode.Name));
        Assert.Equal([old, fresh, mid], DownloadViews.Sort(jobs, SortMode.Size));
        Assert.Equal([fresh, mid, old], DownloadViews.Sort(jobs, SortMode.Status));
    }
}
