using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class ManagerTests
{
    private const int MiB = 1024 * 1024;
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "idmrea-manager-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }

    private static async Task<DownloadJob> Until(DownloadManager manager, Guid id, Func<DownloadJob, bool> done, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true) { var job = manager.Snapshot().Single(j => j.Id == id); if (done(job)) return job; await Task.Delay(20, timeout.Token); }
    }

    private static async Task UntilAll(DownloadManager manager, Func<IReadOnlyList<DownloadJob>, bool> done, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!done(manager.Snapshot())) await Task.Delay(20, timeout.Token);
    }

    private static Task<DownloadJob> Finished(DownloadManager manager, Guid id) =>
        Until(manager, id, j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed);

    private static bool Active(DownloadJob j) => j.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Completed;

    [Fact]
    public async Task TypedFileNameGetsAsciiDigits()
    {
        await using var server = new Fixture();
        var root = Temp();
        await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var job = await manager.AddAsync(server.Url, root, "تقرير ٢٠٢٦ ۳.pdf", startPaused: true);
        Assert.Equal("تقرير 2026 3.pdf", job.FileName);
    }

    /// <summary>Windows warns before opening a downloaded program only when the file carries the internet zone mark.</summary>
    [Fact]
    public async Task FinishedFileIsMarkedAsFromTheInternet()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new Fixture();
        var root = Temp();
        await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var job = await manager.AddAsync(server.Url.Replace("http://", "http://user:secret@"), root);
        var done = await Finished(manager, job.Id);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        var mark = await File.ReadAllTextAsync(done.Destination + ":Zone.Identifier");
        Assert.Contains("[ZoneTransfer]", mark); Assert.Contains("ZoneId=3", mark);
        Assert.Contains("HostUrl=" + server.Url, mark); Assert.DoesNotContain("secret", mark);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
    }

    [Fact]
    public async Task AddUsesProbeNameCategoryAndFolder()
    {
        await using var server = new Fixture { Path = "/get" };
        server.Headers["Content-Disposition"] = "attachment; filename=\"movie.mp4\"";
        var root = Temp(); await File.WriteAllTextAsync(Path.Combine(root, "movie.mp4"), "keep");
        await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var job = await manager.AddAsync(server.Url, root, startPaused: true);
        Assert.Equal(Path.Combine(root, "movie (1).mp4"), job.Destination);
        Assert.Equal(Category.Video, job.Category);
        Assert.True(job.Resumable); Assert.Equal(server.Url, job.ResolvedUrl);
        Assert.Equal(DownloadStatus.Paused, job.Status); Assert.Equal(PausedBy.Person, job.PausedBy);
        Assert.NotEqual(default, job.CreatedAt);
        var named = await manager.AddAsync(server.Url, root, fileName: "my:clip.mp4", startPaused: true);
        Assert.Equal(Path.Combine(root, "my_clip.mp4"), named.Destination);
        var probe = await manager.ProbeAsync(server.Url);
        Assert.Equal("movie.mp4", probe.FileName);
    }

    [Fact]
    public async Task DownloadsAtOnceAppliesLive()
    {
        await using var server = new Fixture { Delay = 20 };
        var root = Temp(); await using var manager = new DownloadManager(Path.Combine(root, "data"));
        await manager.ApplySettingsAsync(new EngineSettings(DownloadsAtOnce: 1));
        for (var i = 0; i < 3; i++) await manager.AddAsync(server.Url, root);
        await UntilAll(manager, jobs => jobs.Count(j => j.Status == DownloadStatus.Downloading) == 1);
        await Task.Delay(300);
        Assert.Equal(1, manager.Snapshot().Count(j => j.Status == DownloadStatus.Downloading));
        Assert.Equal(2, manager.Snapshot().Count(j => j.Status == DownloadStatus.Queued));
        await manager.ApplySettingsAsync(new EngineSettings(DownloadsAtOnce: 3));
        await UntilAll(manager, jobs => jobs.Count(j => j.Status == DownloadStatus.Downloading) == 3, 10);
        Assert.Equal(3, manager.Settings.DownloadsAtOnce);
    }

    [Fact]
    public async Task RetriesThenFails()
    {
        await using var server = new Fixture { Mode = "no-validator", CloseFrom = 2 };
        var root = Temp(); var data = Path.Combine(root, "data"); var time = new ManualTime();
        new JobStore(data).SaveSettings(new EngineSettings(RetryAttempts: 3));
        await using var manager = new DownloadManager(data, new ManagerOptions(Time: time));
        var id = (await manager.AddAsync(server.Url, root)).Id;
        var delays = new[] { 2, 5, 15 };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var waiting = await Until(manager, id, j => j.Status == DownloadStatus.Retrying && j.Attempts == attempt);
            Assert.Equal(ErrorKind.Network, waiting.ErrorKind);
            Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(delays[attempt - 1]), waiting.NextRetryAt);
            time.Advance(TimeSpan.FromSeconds(delays[attempt - 1]) - TimeSpan.FromMilliseconds(1));
            await Task.Delay(100);
            Assert.Equal(DownloadStatus.Retrying, manager.Snapshot().Single().Status);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }
        var failed = await Until(manager, id, j => j.Status == DownloadStatus.Failed);
        Assert.Equal("Can't reach the server. Check your connection.", failed.Error);
        Assert.Equal(ErrorKind.Network, failed.ErrorKind);
        Assert.Equal(3, failed.Attempts);
    }

    [Fact]
    public async Task ProgressResetsAttempts()
    {
        await using var server = new Fixture { Mode = "truncate" };
        var root = Temp(); var data = Path.Combine(root, "data"); var time = new ManualTime();
        var store = new JobStore(data); store.SaveSettings(new EngineSettings(RetryAttempts: 3));
        var dest = Path.Combine(root, "file.bin");
        var saved = new DownloadJob(Guid.NewGuid(), server.Url, dest, dest + ".part", DownloadStatus.Retrying, Attempts: 3, NextRetryAt: time.GetUtcNow() - TimeSpan.FromSeconds(1));
        store.Save(saved);
        await using var manager = new DownloadManager(data, new ManagerOptions(Time: time));
        // Half the file arrives, then the connection keeps dropping: progress means this is attempt 1, not 4.
        var job = await Until(manager, saved.Id, j => j.Status is DownloadStatus.Retrying or DownloadStatus.Failed && j.ReceivedBytes > 0);
        Assert.Equal(DownloadStatus.Retrying, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task RetryAfterIsHonoured()
    {
        await using var server = new Fixture { Status = 429, StatusFrom = 2 };
        server.Headers["Retry-After"] = "7";
        var root = Temp(); var time = new ManualTime();
        await using var manager = new DownloadManager(Path.Combine(root, "data"), new ManagerOptions(Time: time));
        var id = (await manager.AddAsync(server.Url, root)).Id;
        var waiting = await Until(manager, id, j => j.Status == DownloadStatus.Retrying);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(7), waiting.NextRetryAt);
        Assert.Equal(ErrorKind.RateLimited, waiting.ErrorKind); Assert.Equal(429, waiting.HttpStatus); Assert.Equal(1, waiting.Attempts);
    }

    [Fact]
    public async Task DiskFullFailsWithoutRetry()
    {
        await using var server = new Fixture { BodySize = 4 * MiB };
        var root = Temp();
        await using var manager = new DownloadManager(Path.Combine(root, "data"),
            new ManagerOptions(WriteFault: offset => offset >= MiB ? new IOException("full", unchecked((int)0x80070070)) : null));
        var job = await Finished(manager, (await manager.AddAsync(server.Url, root)).Id);
        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.Equal(ErrorKind.DiskFull, job.ErrorKind);
        Assert.Equal(0, job.Attempts);
        Assert.Equal("Not enough disk space.", job.Error);
    }

    [Fact]
    public async Task StartupRule()
    {
        await using var server = new Fixture { Delay = 20 };
        var root = Temp(); var data = Path.Combine(root, "data"); var store = new JobStore(data);
        DownloadJob Make(string name, DownloadStatus status, PausedBy by = PausedBy.None, DateTimeOffset? retryAt = null)
        {
            var dest = Path.Combine(root, name);
            var job = new DownloadJob(Guid.NewGuid(), server.Url, dest, dest + ".part", status, PausedBy: by, NextRetryAt: retryAt, Attempts: retryAt == null ? 0 : 1);
            store.Save(job); return job;
        }
        var running = Make("a.bin", DownloadStatus.Downloading);
        var person = Make("b.bin", DownloadStatus.Paused, PausedBy.Person);
        var engine = Make("c.bin", DownloadStatus.Paused, PausedBy.Engine);
        var retrying = Make("d.bin", DownloadStatus.Retrying, retryAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        await using (var manager = new DownloadManager(data))
        {
            await UntilAll(manager, jobs => jobs.Where(j => j.Id != person.Id).All(Active));
            await Task.Delay(200);
            Assert.Equal(DownloadStatus.Paused, manager.Snapshot().Single(j => j.Id == person.Id).Status);
            Assert.All(manager.Snapshot().Where(j => j.Id != person.Id), j => Assert.True(Active(j), j.Status.ToString()));
        }
        // Closing the engine marks what was running as paused by the engine, so it carries on next time.
        var after = new JobStore(data).Load().ToDictionary(j => j.Id);
        Assert.Equal(PausedBy.Person, after[person.Id].PausedBy);
        foreach (var id in new[] { running.Id, engine.Id, retrying.Id })
            Assert.True(after[id] is { Status: DownloadStatus.Paused, PausedBy: PausedBy.Engine } or { Status: DownloadStatus.Completed }, after[id].ToString());
    }

    [Fact]
    public async Task RemoveWithDeleteRecycles()
    {
        await using var server = new Fixture();
        var root = Temp(); var data = Path.Combine(root, "data"); var recycled = new List<string>(); var removed = new List<Guid>();
        await using var manager = new DownloadManager(data, new ManagerOptions(Recycle: path => { recycled.Add(path); File.Delete(path); return true; }));
        manager.JobRemoved += removed.Add;
        var done = await Finished(manager, (await manager.AddAsync(server.Url, root)).Id);
        Assert.Equal(DownloadStatus.Completed, done.Status); Assert.NotNull(done.CompletedAt);
        await manager.RemoveAsync(done.Id, deleteFile: true);
        Assert.Equal([done.Destination], recycled); Assert.Equal([done.Id], removed);
        Assert.Empty(manager.Snapshot()); Assert.Empty(new JobStore(data).Load());

        var kept = await Finished(manager, (await manager.AddAsync(server.Url, root)).Id);
        await manager.RemoveAsync(kept.Id, deleteFile: false);
        Assert.True(File.Exists(kept.Destination)); Assert.Single(recycled);

        await using var slow = new Fixture { Delay = 20 };
        var running = await manager.AddAsync(slow.Url, root);
        await Until(manager, running.Id, j => j.ReceivedBytes > 0);
        await manager.RemoveAsync(running.Id, deleteFile: true);
        Assert.False(File.Exists(running.PartialPath)); Assert.Empty(manager.Snapshot());
    }

    [Fact]
    public async Task RetryAfterChangedRestartsFromZero()
    {
        await using var server = new Fixture { Mode = "change-after-probe", BodySize = 4 * MiB };
        var root = Temp(); await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var failed = await Finished(manager, (await manager.AddAsync(server.Url, root)).Id);
        Assert.Equal(ErrorKind.Changed, failed.ErrorKind);
        var before = server.Ranges.Count;
        await manager.RetryAsync(failed.Id);
        var done = await Finished(manager, failed.Id);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        Assert.Equal("bytes=0-0", server.Ranges.Skip(before).First());
    }

    [Fact]
    public async Task PauseAllResumeAll()
    {
        await using var server = new Fixture { Delay = 10 };
        var root = Temp(); await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++) ids.Add((await manager.AddAsync(server.Url, root)).Id);
        await UntilAll(manager, jobs => jobs.All(j => j.Status == DownloadStatus.Downloading));
        await manager.PauseAllAsync(PausedBy.Person);
        Assert.All(manager.Snapshot(), j => Assert.Equal((DownloadStatus.Paused, PausedBy.Person), (j.Status, j.PausedBy)));
        Assert.Equal(0, manager.Totals().Active);
        await manager.ResumeAllAsync();
        foreach (var id in ids) Assert.Equal(DownloadStatus.Completed, (await Finished(manager, id)).Status);
    }
}
