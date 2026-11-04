using System.Text;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

/// <summary>Downloads handed over by the browser: they need the page's referrer, cookies and user agent.</summary>
public sealed class BrowserDownloadTests
{
    private const int MiB = 1024 * 1024;
    private static readonly RequestHeaders Browser = new("https://site.example/page", "session=abc123; theme=dark", "TestBrowser/1.0");
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "idmrea-tests-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static async Task<DownloadJob> Wait(DownloadManager manager, Guid id, params DownloadStatus[] states)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true) { var job = manager.Snapshot().Single(j => j.Id == id); if (states.Contains(job.Status)) return job; await Task.Delay(25, timeout.Token); }
    }
    private static void AssertBrowserHeaders(Dictionary<string, string> h)
    {
        Assert.Equal(Browser.Referrer, h.GetValueOrDefault("Referer"));
        Assert.Equal(Browser.Cookies, h.GetValueOrDefault("Cookie"));
        Assert.Equal(Browser.UserAgent, h.GetValueOrDefault("User-Agent"));
    }

    [Fact]
    public async Task HeadersReachTheServerOnProbeAndEveryPart()
    {
        await using var server = new Fixture { BodySize = 8 * MiB }; var dir = Temp();
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        var job = await manager.AddAsync(server.Url, dir, headers: Browser);
        var done = await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(done.Destination));
        Assert.True(done.Parts?.Count > 1, "expected a multi-part download");
        Assert.True(server.RequestHeaders.Count > 2);
        Assert.All(server.RequestHeaders, AssertBrowserHeaders);
    }

    /// <summary>Answers every request on "localhost" with a redirect to <paramref name="target"/>.</summary>
    private static async Task Redirect(System.Net.Sockets.TcpListener listener, string target, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop); } catch (OperationCanceledException) { return; }
            _ = Task.Run(async () =>
            {
                using var _ = client; var stream = client.GetStream(); var buffer = new byte[8192]; var text = "";
                while (!text.Contains("\r\n\r\n")) { var n = await stream.ReadAsync(buffer); if (n == 0) return; text += Encoding.ASCII.GetString(buffer, 0, n); }
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            });
        }
    }

    [Fact]
    public async Task CookiesNeverFollowARedirectToAnotherHost()
    {
        await using var other = new Fixture(); var dir = Temp();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource();
        var redirecting = Redirect(listener, other.Url, stop.Token);
        try
        {
            await using var manager = new DownloadManager(Path.Combine(dir, "data"));
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var job = await manager.AddAsync($"http://localhost:{port}/file.bin", dir, headers: Browser);
            var done = await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed);
            Assert.Equal(DownloadStatus.Completed, done.Status);
            Assert.NotEmpty(other.RequestHeaders);
            Assert.All(other.RequestHeaders, h => Assert.False(h.ContainsKey("Cookie"), "the site's cookies reached another host"));
        }
        finally { stop.Cancel(); listener.Stop(); await redirecting; }
    }

    [Fact]
    public async Task CookiesAreNotStoredInPlainTextAndSurviveARestart()
    {
        if (!OperatingSystem.IsWindows()) return; // elsewhere cookies live in memory only
        await using var server = new Fixture(); var dir = Temp(); var data = Path.Combine(dir, "data");
        Guid id;
        await using (var first = new DownloadManager(data)) id = (await first.AddAsync(server.Url, dir, startPaused: true, headers: Browser)).Id;
        var stored = string.Concat(Directory.GetFiles(data, "jobs2.db*").Select(f =>
        {
            using var file = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream(); file.CopyTo(copy); return Encoding.Latin1.GetString(copy.ToArray());
        }));
        Assert.DoesNotContain("abc123", stored);
        Assert.Contains("site.example", stored); // the referrer is not secret and is kept as is
        var before = server.RequestHeaders.Count;
        await using var second = new DownloadManager(data);
        await second.ControlAsync(id, resume: true);
        Assert.Equal(DownloadStatus.Completed, (await Wait(second, id, DownloadStatus.Completed, DownloadStatus.Failed)).Status);
        var after = server.RequestHeaders.Skip(before).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, AssertBrowserHeaders);
    }

    [Fact]
    public async Task AddWithoutFolderUsesTheTypeFolder()
    {
        await using var server = new Fixture { Path = "/report.pdf" }; var dir = Temp(); var documents = Path.Combine(dir, "Docs");
        await using var manager = new DownloadManager(Path.Combine(dir, "data"));
        await manager.ApplySettingsAsync(manager.Settings with { Folders = new Dictionary<Category, string> { [Category.Documents] = documents } });
        var job = await manager.AddAsync(server.Url, null);
        Assert.Equal(Path.Combine(documents, "report.pdf"), job.Destination);
        Assert.Equal(DownloadStatus.Completed, (await Wait(manager, job.Id, DownloadStatus.Completed, DownloadStatus.Failed)).Status);
    }
}
