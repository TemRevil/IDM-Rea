using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using IDMRea.Contracts;
using IDMRea.Core;
using IDMRea.Engine;
using Xunit;
namespace IDMRea.Tests;

public sealed class LogsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "idmrea-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // the engine tests' job database
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ALineSaysWhenWhatAndWhereWithTheExceptionUnderIt()
    {
        var time = new DateTimeOffset(2026, 11, 5, 19, 42, 1, 123, TimeSpan.FromHours(3));
        Exception error;
        try { throw new InvalidOperationException("pipe broke"); } catch (Exception e) { error = e; }
        var text = LogFile.Format(time, LogLevel.Slow, "Engine", "list took 1.6 s\r\nsecond line", error);
        var lines = text.Split('\n');
        Assert.Equal("2026-11-05 19:42:01.123 +03:00 SLOW    Engine: list took 1.6 s | second line", lines[0]);
        Assert.Equal("    System.InvalidOperationException: pipe broke", lines[1]);
        Assert.All(lines.Skip(1).Where(l => l.Length > 0), l => Assert.StartsWith("    ", l));
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void AFullLogMovesAsideAndOnlyTheNewestFewAreKept()
    {
        var log = new LogFile(dir, "app", maxBytes: 300, keep: 2);
        for (var i = 0; i < 40; i++) log.Write(LogLevel.Info, "Test", $"event number {i:00} with some words to fill the line");
        var files = log.Files().ToList();
        Assert.Equal([Path.Combine(dir, "app.log"), Path.Combine(dir, "app.1.log"), Path.Combine(dir, "app.2.log")], files);
        Assert.False(File.Exists(Path.Combine(dir, "app.3.log")));
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 300));
        Assert.Contains("event number 39", File.ReadAllText(files[0]));
        Assert.DoesNotContain("event number 00", string.Concat(files.Select(File.ReadAllText)));
    }

    [Fact]
    public void TheBundleHoldsEveryLogEvenOneStillOpenForWriting()
    {
        var app = new LogFile(Path.Combine(dir, "a"), "app");
        app.Write(LogLevel.Error, "Test", "it went wrong");
        File.WriteAllText(Path.Combine(dir, "a", "engine.log"), "engine line\n");
        File.WriteAllText(Path.Combine(dir, "a", "notes.txt"), "not a log");
        using var open = new FileStream(app.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        using var zipped = new MemoryStream();
        LogBundle.Write(zipped, [("logs", app.Directory), ("missing", Path.Combine(dir, "nope"))], "IDM-Rea 1.0.0");
        zipped.Position = 0;
        using var zip = new ZipArchive(zipped);
        Assert.Equal(["about.txt", "logs/app.log", "logs/engine.log"], zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("logs/app.log")!.Open());
        Assert.Contains("it went wrong", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("Faulting application name: IDMRea.Engine.exe, version: 1.0.0.0, time stamp: 0x6ac20000\nFaulting module name: KERNELBASE.dll", "engine")]
    [InlineData("Application: IDMRea.App.exe\nCoreCLR Version: 10.0.1326.50504\nException Info: System.ObjectDisposedException", "app")]
    [InlineData("The program IDMRea.App.exe version 1.0.0.0 stopped interacting with Windows and was closed.", "app")]
    [InlineData("Application: IDMRea.BrowserHost.exe\nDescription: The process was terminated due to an unhandled exception.", "browser link")]
    [InlineData("Application: dotnet.exe\nException Info: System.Exception\n   at IDMRea.Engine.EngineHost.RunAsync()", null)]
    [InlineData("Faulting application name: testhost.exe, version: 18.0\nFaulting module path: G:\\IDM-Rea\\IDMRea.App.exe", null)]
    [InlineData("Application: MyIDMRea.App.exe\nDescription: something else", null)]
    public void OnlyWindowsReportsAboutIdmReasOwnProgramsCount(string report, string? part) => Assert.Equal(part, CrashReports.PartOf(report));

    [Fact]
    public async Task AFailedDownloadIsLoggedOnceWithoutTheLinksSecrets()
    {
        await using var server = new Fixture { Status = 404, StatusFrom = 2, Path = "/report.pdf" };
        await using var engine = await LoggedEngine.StartAsync(dir);
        var url = server.Url + "?token=s3cret";
        Assert.True((await engine.Client.SendAsync(new("add", url, Path.Combine(dir, "files"), Cookies: "session=c00kie"))).Ok);
        await engine.UntilAsync(j => j.Status == DownloadStatus.Failed);
        Assert.True((await engine.Client.SendAsync(new("list"))).Ok);

        var text = engine.Text();
        var line = Assert.Single(text.Split('\n'), l => l.Contains("report.pdf"));
        Assert.Contains("WARNING", line);
        Assert.Contains("127.0.0.1", line);
        Assert.Contains("404", line);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("c00kie", text);
    }

    [Fact]
    public async Task ARequestThatFailsIsLoggedAndOnlyAnUnexpectedOneCarriesItsStack()
    {
        await using var engine = await LoggedEngine.StartAsync(dir, new BrokenShell());
        Assert.False((await engine.Client.SendAsync(new("pause"))).Ok);
        Assert.False((await engine.Client.SendAsync(new("registerBrowser"))).Ok);

        var lines = engine.Text().Split('\n');
        var pause = Assert.Single(lines, l => l.Contains("pause"));
        Assert.Contains("WARNING", pause);
        Assert.Contains("Missing job ID.", pause);
        var register = Array.FindIndex(lines, l => l.Contains("registerBrowser"));
        Assert.Contains("ERROR", lines[register]);
        Assert.StartsWith("    System.NullReferenceException", lines[register + 1]);
    }

    [Fact]
    public async Task TheClientLogsAnEngineThatHangsUpButNotACallTheCallerCancelled()
    {
        using var data = new DataFolder(dir);
        var log = new LogFile(Path.Combine(dir, "logs"), "app");
        var client = new EngineClient { Log = log };
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new("list"), cancelled.Token));
        Assert.False(File.Exists(log.Path));

        var serving = HangUpOnce();
        await Assert.ThrowsAnyAsync<IOException>(() => client.SendAsync(new("list")));
        await serving;
        var line = Assert.Single(File.ReadAllLines(log.Path), l => !l.StartsWith(' '));
        Assert.Contains("WARNING", line);
        Assert.Contains("list", line);
    }

    [Fact]
    public async Task AnEngineThatWontStartIsLoggedButNotTheCheckForARunningOne()
    {
        using var data = new DataFolder(dir);
        var log = new LogFile(Path.Combine(dir, "logs"), "app");
        var client = new EngineClient { Log = log };
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.EnsureStartedAsync(Path.Combine(dir, "missing", "IDMRea.Engine.exe")));
        var line = Assert.Single(File.ReadAllLines(log.Path), l => !l.StartsWith(' '));
        Assert.Contains("ERROR", line);
        Assert.Contains("didn't start", line);
    }

    [Fact]
    public async Task OnlyACallThatShouldBeQuickIsLoggedAsSlow()
    {
        using var data = new DataFolder(dir);
        var log = new LogFile(Path.Combine(dir, "logs"), "app");
        var client = new EngineClient { Log = log };
        foreach (var command in new[] { "list", "probe" })
        {
            var serving = AnswerSlowly(EngineClient.Slow + TimeSpan.FromMilliseconds(300));
            Assert.True((await client.SendAsync(new(command, Url: "http://example.com/a.zip"))).Ok);
            await serving;
        }
        var line = Assert.Single(File.ReadAllLines(log.Path));
        Assert.Contains("SLOW", line);
        Assert.Contains("list took", line);
    }

    private static NamedPipeServerStream Pipe() => new(Protocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task HangUpOnce()
    {
        await using var pipe = Pipe();
        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task AnswerSlowly(TimeSpan wait)
    {
        await using var pipe = Pipe();
        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await reader.ReadLineAsync();
        await Task.Delay(wait);
        await writer.WriteLineAsync("{\"ok\":true}");
    }

    /// <summary>Points IDMREA_DATA_DIRECTORY (and so the engine pipe) at a folder of the test's own.</summary>
    private sealed class DataFolder : IDisposable
    {
        private readonly string? previous = Environment.GetEnvironmentVariable("IDMREA_DATA_DIRECTORY");
        public DataFolder(string root) => Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", Path.Combine(root, "data"));
        public void Dispose() => Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", previous);
    }

    private sealed class BrokenShell : IEngineShell
    {
        public void Notify(DownloadJob job) { }
        public void SettingsChanged(EngineSettings settings) { }
        public bool Recycle(string path) => true;
        public void RegisterBrowser() => throw new NullReferenceException();
    }

    /// <summary>An engine host in this process, writing its log to the data folder's Logs folder.</summary>
    private sealed class LoggedEngine : IAsyncDisposable
    {
        private readonly DataFolder data;
        private readonly CancellationTokenSource stop = new();
        private readonly DownloadManager manager;
        private readonly Task run;
        private readonly LogFile log;
        public EngineClient Client { get; } = new();

        private LoggedEngine(string root, IEngineShell shell)
        {
            data = new DataFolder(root);
            manager = new DownloadManager(Protocol.DataDirectory);
            log = new LogFile(Protocol.LogsDirectory, "engine");
            run = new EngineHost(manager, shell, log: log).RunAsync(stop.Token);
        }

        public static async Task<LoggedEngine> StartAsync(string root, IEngineShell? shell = null)
        {
            var engine = new LoggedEngine(root, shell ?? new NullShell());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try { if ((await engine.Client.SendAsync(new("list"), timeout.Token)).Ok) return engine; }
                catch (IOException) { await Task.Delay(20, timeout.Token); }
            }
        }

        public async Task UntilAsync(Func<DownloadJob, bool> done)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!manager.Snapshot().Any(done)) await Task.Delay(20, timeout.Token);
            await Task.Delay(100, timeout.Token);
        }

        public string Text() => File.Exists(log.Path) ? File.ReadAllText(log.Path) : "";

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await run; await manager.DisposeAsync(); stop.Dispose(); data.Dispose();
        }
    }
}
