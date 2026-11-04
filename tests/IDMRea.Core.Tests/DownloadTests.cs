using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using IDMRea.Contracts;
using IDMRea.Core;
using IDMRea.App;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace IDMRea.Tests;

public sealed class DownloadTests
{
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "idmrea-tests-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static async Task<DownloadJob> Wait(DownloadManager manager, Guid id, params DownloadStatus[] states)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true) { var job = manager.Snapshot().Single(j => j.Id == id); if (states.Contains(job.Status)) return job; await Task.Delay(25, timeout.Token); }
    }
    [Fact]
    public async Task CompletionPreservesExistingFile()
    {
        await using var server = new Fixture(); var dir = Temp(); await File.WriteAllTextAsync(Path.Combine(dir, "file.bin"), "existing");
        await using var manager = new DownloadManager(Path.Combine(dir, "data")); var job = await manager.AddAsync(server.Url, dir);
        var done = await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed); Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination)); Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(dir, "file.bin")));
    }
    [Theory]
    [InlineData("range")][InlineData("ignore")][InlineData("change-full")][InlineData("bad-range")][InlineData("bad-validator")][InlineData("no-validator")][InlineData("extra-tail")][InlineData("last-modified")][InlineData("short-part")][InlineData("complete-checkpoint")][InlineData("oversized-checkpoint")]
    public async Task ResumeChecksRepresentationAndCheckpoint(string mode)
    {
        await using var server = new Fixture { Mode = mode }; var dir = Temp(); var data = Path.Combine(dir, "data");
        var destination = Path.Combine(dir, "file.bin"); var part = destination + ".part"; const int checkpoint = 65536;
        var bytes = mode == "change-full" ? RandomNumberGenerator.GetBytes(checkpoint) : server.Body[..(mode == "extra-tail" ? checkpoint + 100 : mode == "short-part" ? checkpoint / 2 : checkpoint)];
        await File.WriteAllBytesAsync(part, bytes);
        var job = new DownloadJob(Guid.NewGuid(), server.Url, destination, part, DownloadStatus.Paused, checkpoint, mode == "complete-checkpoint" ? checkpoint : mode == "oversized-checkpoint" ? checkpoint - 1 : server.Body.Length, mode is "no-validator" or "last-modified" ? null : "\"v1\"", mode == "last-modified" ? DateTimeOffset.Parse("2026-01-01T00:00:00Z") : null);
        new JobStore(data).Save(job);
        await using var manager = new DownloadManager(data); await manager.ControlAsync(job.Id, resume: true);
        var done = await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed);
        if (mode.StartsWith("bad")) { Assert.Equal(DownloadStatus.Failed, done.Status); Assert.False(File.Exists(destination)); }
        else { Assert.Equal(DownloadStatus.Completed, done.Status); Assert.Equal(server.Body, await File.ReadAllBytesAsync(destination)); }
        Assert.Equal(mode is "no-validator" or "complete-checkpoint" or "oversized-checkpoint" ? null : mode == "short-part" ? "bytes=32768-" : "bytes=65536-", server.LastRange);
    }
    [Fact]
    public async Task PauseRestartResumeAndCancel()
    {
        await using var server = new Fixture { Delay = 15 }; var dir = Temp(); var data = Path.Combine(dir, "data"); Guid id;
        await using (var manager = new DownloadManager(data))
        {
            id = (await manager.AddAsync(server.Url, dir)).Id;
            await Wait(manager, id, DownloadStatus.Downloading); await Task.Delay(150); await manager.ControlAsync(id);
            var paused = manager.Snapshot().Single(); Assert.Equal(DownloadStatus.Paused, paused.Status); Assert.True(paused.ReceivedBytes > 0);
        }
        await using (var manager = new DownloadManager(data))
        { await manager.ControlAsync(id, resume: true); var done = await Wait(manager, id, DownloadStatus.Completed, DownloadStatus.Failed); Assert.Equal(DownloadStatus.Completed, done.Status); Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination)); }
        await using (var manager = new DownloadManager(data))
        { var next = await manager.AddAsync(server.Url, dir); await manager.ControlAsync(next.Id, cancel: true); Assert.False(File.Exists(next.PartialPath)); Assert.Equal(DownloadStatus.Cancelled, manager.Snapshot().Single(j => j.Id == next.Id).Status); }
    }
    [Fact]
    public async Task TruncatedResponseFailsWithoutPublication()
    {
        await using var server = new Fixture { Mode = "truncate" }; var dir = Temp(); new JobStore(Path.Combine(dir, "data")).SaveSettings(new EngineSettings(RetryAttempts: 0)); await using var manager = new DownloadManager(Path.Combine(dir,"data"));
        var job = await manager.AddAsync(server.Url, dir); var failed = await Wait(manager, job.Id, DownloadStatus.Failed); Assert.False(File.Exists(failed.Destination));
        await manager.ControlAsync(job.Id, cancel: true); Assert.False(File.Exists(failed.PartialPath));
    }
    [Theory]
    [InlineData("CON.txt", "_CON.txt")][InlineData("a<b>.zip", "a_b_.zip")][InlineData("LPT9", "_LPT9")][InlineData("CON .txt", "_CON .txt")]
    public void WindowsNames(string source, string expected) => Assert.Equal(expected, DownloadManager.SafeName(source));
    [Theory]
    [InlineData("before")][InlineData("after")][InlineData("occupied")]
    public async Task PublicationRecovery(string mode)
    {
        var dir = Temp(); var data = Path.Combine(dir, "data"); var bytes = RandomNumberGenerator.GetBytes(4096);
        var dest = Path.Combine(dir, "final.bin"); var part = dest + ".part";
        await File.WriteAllBytesAsync(mode == "after" ? dest : part, bytes);
        if (mode == "occupied") await File.WriteAllTextAsync(dest, "unrelated");
        var job = new DownloadJob(Guid.NewGuid(), "http://localhost/test", dest, part, DownloadStatus.Finalizing, bytes.Length, bytes.Length, CompletionFileId: FileIdentity.Of(mode == "after" ? dest : part));
        new JobStore(data).Save(job); await using var manager = new DownloadManager(data); var recovered = manager.Snapshot().Single();
        Assert.Equal(mode == "occupied" ? DownloadStatus.Failed : DownloadStatus.Completed, recovered.Status);
        if (mode == "occupied") Assert.Equal("unrelated", await File.ReadAllTextAsync(dest)); else Assert.Equal(bytes, await File.ReadAllBytesAsync(dest));
        if (mode == "occupied") { Assert.Equal(ErrorKind.NameTaken, recovered.ErrorKind); Assert.Equal(Text.Describe(ErrorKind.NameTaken, null), recovered.Error); }
    }

    /// <summary>A finished file that vanished before it was saved fails plainly, and Retry downloads it again from the start.</summary>
    [Fact]
    public async Task LostFinishedFileIsDownloadedAgainOnRetry()
    {
        await using var server = new Fixture(); var dir = Temp(); var data = Path.Combine(dir, "data");
        var dest = Path.Combine(dir, "final.bin"); var part = dest + ".part";
        new JobStore(data).Save(new DownloadJob(Guid.NewGuid(), server.Url, dest, part, DownloadStatus.Finalizing, server.BodySize, server.BodySize, "\"v1\"", Parts: [new DownloadPart(0, server.BodySize - 1, server.BodySize)], Resumable: true));
        await using var manager = new DownloadManager(data);
        var lost = manager.Snapshot().Single();
        Assert.Equal((DownloadStatus.Failed, ErrorKind.Lost), (lost.Status, lost.ErrorKind));
        Assert.Equal(Text.Describe(ErrorKind.Lost, null), lost.Error);
        await manager.RetryAsync(lost.Id);
        var done = await Wait(manager, lost.Id, DownloadStatus.Completed, DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(dest));
    }
    /// <summary>The finished file's identity is Windows' file ID, which stays with the file when it is moved into place.</summary>
    [Fact]
    public async Task CompletionRecordsAFileIdThatSurvivesTheMove()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new Fixture(); var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data")); var job = await manager.AddAsync(server.Url, dir);
        var done = await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.NotNull(done.CompletionFileId);
        Assert.Equal(FileIdentity.Of(done.Destination), done.CompletionFileId);
    }

    /// <summary>After a crash, a file of the same size that is not ours is never claimed as the download.</summary>
    [Fact]
    public async Task RecoveryIgnoresAnUnrelatedFileOfTheSameSize()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Temp(); var data = Path.Combine(dir, "data"); var bytes = RandomNumberGenerator.GetBytes(4096);
        var dest = Path.Combine(dir, "final.bin"); var part = dest + ".part";
        await File.WriteAllBytesAsync(part, bytes); var ours = FileIdentity.Of(part); File.Delete(part);
        var unrelated = RandomNumberGenerator.GetBytes(4096); await File.WriteAllBytesAsync(dest, unrelated);
        new JobStore(data).Save(new DownloadJob(Guid.NewGuid(), "http://localhost/test", dest, part, DownloadStatus.Finalizing, bytes.Length, bytes.Length, CompletionFileId: ours));
        await using var manager = new DownloadManager(data);
        Assert.Equal(DownloadStatus.Failed, manager.Snapshot().Single().Status);
        Assert.Equal(unrelated, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task RealClientProcessProtocolPersistenceAndOwnership()
    {
        var dir = Temp(); var old = Environment.GetEnvironmentVariable("IDMREA_DATA_DIRECTORY"); Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", dir);
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !Directory.Exists(Path.Combine(root.FullName, ".git"))) root = root.Parent;
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var engine = Path.Combine(root!.FullName, "src", "IDMRea.Engine", "bin", config, "net10.0", "IDMRea.Engine.dll");
        Process Start() => Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { ArgumentList = { engine }, UseShellExecute = false })!;
        Process? process = null;
        try
        {
            var client = new EngineClient(); process = Start(); Assert.True((await client.SendAsync(new("list"))).Ok);
            using (var second = Start()) { await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(2, second.ExitCode); }
            Assert.False((await client.SendAsync(new("list", Version: 99))).Ok); Assert.False((await client.SendAsync(new("add", "file:///secret", dir))).Ok);
            await using var server = new Fixture(); Assert.True((await client.SendAsync(new("add", server.Url, dir))).Ok);
            EngineResponse response;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            do { await Task.Delay(40, timeout.Token); response = await client.SendAsync(new("list")); } while (!response.Jobs!.Any(j => j.Status is DownloadStatus.Completed or DownloadStatus.Failed));
            var completed = Assert.Single(response.Jobs!); Assert.Equal(DownloadStatus.Completed, completed.Status); Assert.Equal(server.Body, await File.ReadAllBytesAsync(completed.Destination));
            Assert.True((await client.SendAsync(new("shutdown"))).Ok); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); process.Dispose();
            process = Start(); Assert.Equal(DownloadStatus.Completed, Assert.Single((await client.SendAsync(new("list"))).Jobs!).Status);
            await client.SendAsync(new("shutdown")); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (process != null) { if (!process.HasExited) process.Kill(true); process.Dispose(); } Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", old); }
    }
}
