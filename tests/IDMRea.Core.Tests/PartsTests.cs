using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class PartsTests
{
    private const int MiB = 1024 * 1024;
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "idmrea-parts-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }

    private static async Task<DownloadJob> Until(DownloadManager manager, Guid id, Func<DownloadJob, bool> done, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { var job = manager.Snapshot().Single(j => j.Id == id); if (done(job)) return job; await Task.Delay(20, timeout.Token); }
    }

    private static Task<DownloadJob> Finished(DownloadManager manager, Guid id) =>
        Until(manager, id, j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed);

    private static async Task<(DownloadJob Job, string Root)> Download(Fixture server, EngineSettings? settings = null)
    {
        var root = Temp(); var data = Path.Combine(root, "data");
        if (settings != null) new JobStore(data).SaveSettings(settings);
        await using var manager = new DownloadManager(data);
        var job = await manager.AddAsync(server.Url, root);
        return (await Finished(manager, job.Id), root);
    }

    [Fact]
    public void PlannerInitialAndSplit()
    {
        var parts = PartPlanner.Initial(8 * MiB, 8, MiB);
        Assert.Equal(8, parts.Count);
        Assert.Equal(0, parts[0].Start); Assert.Equal(8 * MiB - 1, parts[^1].End);
        for (var i = 1; i < parts.Count; i++) Assert.Equal(parts[i - 1].End + 1, parts[i].Start);
        Assert.All(parts, p => { Assert.Equal(0, p.Received); Assert.Equal(MiB, p.Length); });

        var odd = PartPlanner.Initial(8 * MiB + 5, 8, MiB);
        Assert.Equal(8 * MiB + 4, odd[^1].End); Assert.Equal(odd.Sum(p => p.Length), 8 * MiB + 5);
        var small = Assert.Single(PartPlanner.Initial(600 * 1024, 8, MiB)); Assert.Equal(600 * 1024 - 1, small.End);
        Assert.Empty(PartPlanner.Initial(0, 8, MiB));
        Assert.Equal(8, PartPlanner.Initial(100L * MiB, 8, MiB).Count);
        Assert.Equal(3, PartPlanner.Initial(3 * MiB + 10, 8, MiB).Count);

        var running = new[] { new DownloadPart(0, 4 * MiB - 1, 0), new DownloadPart(4 * MiB, 8 * MiB - 1, 3 * MiB), new DownloadPart(8 * MiB, 11 * MiB - 1, 0) };
        Assert.Equal(0, PartPlanner.PickToSplit(running, new HashSet<int> { 0, 1 }));
        Assert.Equal(2, PartPlanner.PickToSplit(running, new HashSet<int> { 1, 2 }));
        Assert.Null(PartPlanner.PickToSplit(running, new HashSet<int> { 1 }));
        Assert.Null(PartPlanner.PickToSplit([new DownloadPart(0, 2 * MiB - 1, 0)], new HashSet<int> { 0 }));
        Assert.Equal(0, PartPlanner.PickToSplit([new DownloadPart(0, 2 * MiB, 0)], new HashSet<int> { 0 }));
    }

    [Fact]
    public async Task MultiPartExactBytes()
    {
        await using var server = new Fixture { BodySize = 8 * MiB, Delay = 1, ChunkSize = 32 * 1024 };
        var (done, _) = await Download(server);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        // One connection closing while the next opens can show 2 at the server, so more than 2 proves real parallel parts.
        Assert.True(server.MaxConcurrent > 2, $"max concurrent {server.MaxConcurrent}");
        Assert.NotNull(done.Parts); Assert.All(done.Parts!, p => Assert.True(p.Done));
        Assert.Equal(8 * MiB, done.ReceivedBytes);
    }

    /// <summary>
    /// Windows zero-fills a file up to any offset written past its valid data, so a part far into a pre-sized
    /// file would wait for that (about 10 s per 448 MB on a USB disk). A sparse part file skips it; the finished
    /// file is no longer marked sparse.
    /// </summary>
    [Fact]
    public async Task PartFileIsSparseWhileDownloadingAndNotAfter()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new Fixture { BodySize = 8 * MiB, Delay = 1, ChunkSize = 32 * 1024 };
        var root = Temp(); var data = Path.Combine(root, "data");
        string? partial = null; var sparseSeen = false;
        await using var manager = new DownloadManager(data, new ManagerOptions(WriteFault: _ =>
        {
            if (partial != null && File.GetAttributes(partial).HasFlag(FileAttributes.SparseFile)) sparseSeen = true;
            return null;
        }));
        var job = await manager.AddAsync(server.Url, root);
        partial = job.PartialPath;
        var done = await Finished(manager, job.Id);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.True(sparseSeen, "the part file was never sparse while parts were written");
        Assert.False(File.GetAttributes(done.Destination).HasFlag(FileAttributes.SparseFile));
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
    }

    [Fact]
    public async Task MultiPartResumeAfterKill()
    {
        await using var server = new Fixture { BodySize = 8 * MiB, Delay = 1 };
        var root = Temp(); var data = Path.Combine(root, "data"); Guid id;
        await using (var manager = new DownloadManager(data))
        {
            id = (await manager.AddAsync(server.Url, root)).Id;
            await Until(manager, id, j => j.ReceivedBytes > 0.3 * 8 * MiB);
        }
        var saved = Assert.Single(new JobStore(data).Load());
        Assert.Equal(DownloadStatus.Paused, saved.Status);
        Assert.NotNull(saved.Parts);
        Assert.Equal(saved.ReceivedBytes, saved.Parts!.Sum(p => p.Received));
        var unfinished = saved.Parts!.Where(p => !p.Done).ToList(); Assert.NotEmpty(unfinished);
        var before = server.Ranges.Count;
        await using (var manager = new DownloadManager(data))
        {
            await manager.ControlAsync(id, resume: true);
            var done = await Finished(manager, id);
            Assert.Equal(DownloadStatus.Completed, done.Status);
            Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        }
        var after = server.Ranges.Skip(before).ToList();
        foreach (var part in unfinished) Assert.Contains($"bytes={part.Start + part.Received}-{part.End}", after);
        Assert.DoesNotContain("bytes=0-0", after);
    }

    [Fact]
    public async Task DynamicSplitHappens()
    {
        await using var server = new Fixture { BodySize = 16 * MiB, Delay = 1, SlowFirstPart = true };
        var (done, _) = await Download(server, new EngineSettings(PartsPerFile: 2));
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        Assert.True(done.Parts!.Count > 2, $"parts {done.Parts!.Count}");
        Assert.Equal(16 * MiB, done.Parts!.Sum(p => p.Length));
    }

    [Fact]
    public async Task FallsBackWhenPartsGet200()
    {
        await using var server = new Fixture { Mode = "no-ranges-on-parts", BodySize = 4 * MiB };
        var (done, _) = await Download(server);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        Assert.True(done.Parts is null or { Count: 0 });
    }

    [Fact]
    public async Task ValidatorChangeMidwayStops()
    {
        await using var server = new Fixture { Mode = "change-after-probe", BodySize = 4 * MiB };
        var (done, _) = await Download(server);
        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.Equal(ErrorKind.Changed, done.ErrorKind);
        Assert.False(File.Exists(done.Destination));
    }

    [Fact]
    public async Task BusyServerReducesParts()
    {
        await using var server = new Fixture { Mode = "busy-extra", BodySize = 4 * MiB, Delay = 20, ChunkSize = 32 * 1024 };
        var (done, _) = await Download(server);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        Assert.True(server.MaxConcurrent > 2, "the server never got busy");
        Assert.Null(done.Error); Assert.Null(done.HttpStatus);
    }

    [Fact]
    public async Task DroppedPartReconnects()
    {
        await using var server = new Fixture { Mode = "drop-part-once", BodySize = 4 * MiB };
        var (done, _) = await Download(server);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        Assert.True(server.Dropped);
    }

    [Fact]
    public async Task EmptyAndSmallFiles()
    {
        await using (var empty = new Fixture { BodySize = 0 })
        {
            var (done, _) = await Download(empty);
            Assert.Equal(DownloadStatus.Completed, done.Status);
            Assert.Equal(0, new FileInfo(done.Destination).Length);
        }
        await using var small = new Fixture { BodySize = 600 * 1024 };
        var (job, _) = await Download(small);
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.Equal(small.Body, await File.ReadAllBytesAsync(job.Destination));
        Assert.Single(job.Parts!);
    }

    [Fact]
    public async Task RedirectedPartsUseFinalUrl()
    {
        await using var server = new Fixture { RedirectOnce = true, BodySize = 4 * MiB };
        var (done, _) = await Download(server);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        var targets = server.Targets.ToArray();
        Assert.True(targets.Length > 2);
        Assert.Equal("/file.bin", targets[0]);
        Assert.All(targets.Skip(1), t => Assert.Equal("/moved/file.bin", t));
        Assert.Equal(server.Url.Replace("/file.bin", "/moved/file.bin"), done.ResolvedUrl);
    }
}
