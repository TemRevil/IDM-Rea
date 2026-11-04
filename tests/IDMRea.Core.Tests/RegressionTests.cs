using System.Security.Cryptography;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;

namespace IDMRea.Tests;

public sealed class RegressionTests
{
    private static string Temp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idmrea-review-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<DownloadJob> Finished(DownloadManager manager, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var job = manager.Snapshot().Single(j => j.Id == id);
            if (job.Status is DownloadStatus.Completed or DownloadStatus.Failed) return job;
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task ResumeOfAnActiveDownloadDoesNotRestartItsRequest()
    {
        await using var server = new Fixture { Delay = 15 };
        var root = Temp();
        await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var job = await manager.AddAsync(server.Url, root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (manager.Snapshot().Single().ReceivedBytes == 0) await Task.Delay(10, deadline.Token);
        var requests = server.RequestCount;
        await manager.ControlAsync(job.Id, resume: true);
        await Task.Delay(100);
        Assert.Equal(requests, server.RequestCount);
    }

    [Fact]
    public async Task ResumeRejectsAnOccupiedDestinationBeforeStartingNetworkWork()
    {
        await using var server = new Fixture();
        var root = Temp();
        var data = Path.Combine(root, "data");
        var destination = Path.Combine(root, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, server.Body[..65536]);
        await File.WriteAllTextAsync(destination, "preserve this file");
        var job = new DownloadJob(Guid.NewGuid(), server.Url, destination, part,
            DownloadStatus.Paused, 65536, server.Body.Length, "\"v1\"");
        new JobStore(data).Save(job);
        await using var manager = new DownloadManager(data);
        await Assert.ThrowsAsync<IOException>(() => manager.ControlAsync(job.Id, resume: true));
        Assert.Equal(0, server.RequestCount);
        Assert.Equal(DownloadStatus.Paused, Assert.Single(manager.Snapshot()).Status);
        Assert.Equal("preserve this file", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task UnknownLengthCompletionRecordsTheObservedTotal()
    {
        await using var server = new Fixture { Mode = "unknown-length" };
        var root = Temp();
        await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var job = await manager.AddAsync(server.Url, root);
        var done = await Finished(manager, job.Id);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body.Length, done.TotalBytes);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
    }

    [Fact]
    public async Task AnOversizedRangeResponseIsStoppedBeforeExcessBytesReachDisk()
    {
        await using var server = new Fixture { Mode = "overrun-range" };
        var root = Temp();
        var data = Path.Combine(root, "data");
        var destination = Path.Combine(root, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, server.Body[..65536]);
        var job = new DownloadJob(Guid.NewGuid(), server.Url, destination, part,
            DownloadStatus.Paused, 65536, server.Body.Length, "\"v1\"");
        new JobStore(data).Save(job);
        await using var manager = new DownloadManager(data);
        await manager.ControlAsync(job.Id, resume: true);
        var failed = await Finished(manager, job.Id);
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        Assert.False(File.Exists(destination));
        Assert.InRange(new FileInfo(part).Length, 65536, server.Body.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationRecoveryRestoresAnUnknownTotal(bool moved)
    {
        var root = Temp();
        var destination = Path.Combine(root, "file.bin");
        var part = destination + ".part";
        var body = RandomNumberGenerator.GetBytes(2048);
        await File.WriteAllBytesAsync(moved ? destination : part, body);
        var job = new DownloadJob(Guid.NewGuid(), "https://example.test/file", destination, part,
            DownloadStatus.Finalizing, body.Length, CompletionFileId: FileIdentity.Of(moved ? destination : part));
        var data = Path.Combine(root, "data");
        new JobStore(data).Save(job);
        await using var manager = new DownloadManager(data);
        var recovered = Assert.Single(manager.Snapshot());
        Assert.Equal(DownloadStatus.Completed, recovered.Status);
        Assert.Equal(body.Length, recovered.TotalBytes);
    }

    [Fact]
    public async Task CancelledDownloadDoesNotReportDeletedBytesAsRetainedProgress()
    {
        await using var server = new Fixture { Mode = "truncate" };
        var root = Temp();
        new JobStore(Path.Combine(root, "data")).SaveSettings(new EngineSettings(RetryAttempts: 0));
        await using var manager = new DownloadManager(Path.Combine(root, "data"));
        var job = await manager.AddAsync(server.Url, root);
        await Finished(manager, job.Id);
        await manager.ControlAsync(job.Id, cancel: true);
        var cancelled = Assert.Single(manager.Snapshot());
        Assert.Equal(DownloadStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, cancelled.ReceivedBytes);
        Assert.False(File.Exists(cancelled.PartialPath));
    }
}
