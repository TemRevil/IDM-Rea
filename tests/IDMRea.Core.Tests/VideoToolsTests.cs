using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class VideoToolsTests : IDisposable
{
    private const string Host = "https://releases.test";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "idmrea-tools-" + Guid.NewGuid());
    private readonly FakeReleases web = new();
    private readonly ManualTime time = new();

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    private static ToolSources Sources => new(
        $"{Host}/api/yt-dlp", $"{Host}/api/deno", $"{Host}/api/ffmpeg",
        $"{Host}/yt-dlp/yt-dlp/releases/download/", $"{Host}/denoland/deno/releases/download/", $"{Host}/yt-dlp/FFmpeg-Builds/releases/download/");

    private VideoTools Tools() => new(dir, new HttpClient(web), Sources, time);

    private static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
        return memory.ToArray();
    }

    /// <summary>Publishes one release of each tool, in the layouts the real releases use.</summary>
    private void Publish(string ytdlpTag = "2026.10.26", byte[]? ytdlp = null, string? ytdlpSum = null, string? denoSum = null)
    {
        ytdlp ??= Encoding.UTF8.GetBytes("yt-dlp " + ytdlpTag);
        var deno = Zip(("deno.exe", "deno 2.9.8"));
        var ffmpeg = Zip(("ffmpeg-master-latest-win64-gpl-shared/bin/ffmpeg.exe", "ffmpeg"), ("ffmpeg-master-latest-win64-gpl-shared/bin/avcodec-62.dll", "codec"),
            ("ffmpeg-master-latest-win64-gpl-shared/doc/readme.txt", "docs"));
        var y = $"{Host}/yt-dlp/yt-dlp/releases/download/{ytdlpTag}/";
        var d = $"{Host}/denoland/deno/releases/download/v2.9.8/";
        var f = $"{Host}/yt-dlp/FFmpeg-Builds/releases/download/latest/";
        web.Release($"{Host}/api/yt-dlp", ytdlpTag, (y + "yt-dlp.exe", ytdlp), (y + "SHA2-256SUMS",
            Encoding.UTF8.GetBytes(ytdlpSum ?? $"{Hex(Encoding.UTF8.GetBytes("other"))}  yt-dlp_x86.exe\n{Hex(ytdlp)}  yt-dlp.exe\n")));
        web.Release($"{Host}/api/deno", "v2.9.8", (d + "deno-x86_64-pc-windows-msvc.zip", deno), (d + "deno-x86_64-pc-windows-msvc.zip.sha256sum",
            Encoding.UTF8.GetBytes(denoSum ?? $"\r\nAlgorithm : SHA256\r\nHash      : {Hex(deno).ToUpperInvariant()}\r\nPath      : C:\\a\\deno\\deno-x86_64-pc-windows-msvc.zip\r\n")));
        web.Release($"{Host}/api/ffmpeg", "latest", (f + "ffmpeg-master-latest-win64-gpl-shared.zip", ffmpeg), (f + "checksums.sha256",
            Encoding.UTF8.GetBytes($"{Hex(ffmpeg)}  ffmpeg-master-latest-win64-gpl-shared.zip\n")));
    }

    [Fact]
    public async Task ToolsInstallFromTheirReleasesWhenTheChecksumsMatch()
    {
        Publish();
        var tools = Tools();
        var seen = new List<ToolProgress>();
        await tools.EnsureAsync(new SyncProgress<ToolProgress>(seen.Add), default);
        Assert.Equal("yt-dlp 2026.10.26", File.ReadAllText(tools.YtDlp));
        Assert.Equal("deno 2.9.8", File.ReadAllText(tools.Deno));
        Assert.Equal("ffmpeg", File.ReadAllText(tools.Ffmpeg));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(tools.Ffmpeg)!, "avcodec-62.dll")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(tools.Ffmpeg)!, "readme.txt")));
        var status = tools.Status();
        Assert.True(status.Ready);
        Assert.Equal("2026.10.26", status.YtDlpVersion);
        Assert.Equal(100, seen[^1].Percent);
    }

    [Fact]
    public async Task AChecksumMismatchInstallsNothing()
    {
        Publish(ytdlpSum: $"{Hex(Encoding.UTF8.GetBytes("tampered"))}  yt-dlp.exe\n");
        var tools = Tools();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => tools.EnsureAsync(null, default));
        Assert.Equal("yt-dlp.exe did not match its published checksum, so it was not installed.", error.Message);
        Assert.False(File.Exists(tools.YtDlp));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "tools"), "*.new", SearchOption.AllDirectories));
        Assert.False(tools.Status().Ready);
    }

    [Fact]
    public async Task AFileFromAnywhereButItsReleaseIsRefused()
    {
        Publish();
        web.Release($"{Host}/api/yt-dlp", "2026.10.26", ("https://elsewhere.test/yt-dlp.exe", [1]), ($"{Host}/yt-dlp/yt-dlp/releases/download/2026.10.26/SHA2-256SUMS", [2]));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Tools().EnsureAsync(null, default));
        Assert.Equal("yt-dlp.exe is not on its official release page, so it was not installed.", error.Message);
    }

    [Fact]
    public async Task DenoSumInPowerShellLayoutIsRead()
    {
        Publish(denoSum: "Algorithm : SHA256\nHash      : " + new string('A', 64) + "\n");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Tools().EnsureAsync(null, default));
        Assert.Equal("deno-x86_64-pc-windows-msvc.zip did not match its published checksum, so it was not installed.", error.Message);
    }

    [Fact]
    public async Task YtDlpIsUpdatedAtMostOnceADayAndNeverWhileInUse()
    {
        Publish();
        var tools = Tools();
        await tools.EnsureAsync(null, default);
        Publish("2026.11.04");
        Assert.False(await tools.UpdateYtDlpAsync(default)); // checked today already, at install
        time.Advance(TimeSpan.FromHours(25));
        using (tools.Use())
        {
            Assert.False(await tools.UpdateYtDlpAsync(default)); // fetched and checked, but yt-dlp is running
            Assert.Equal("yt-dlp 2026.10.26", File.ReadAllText(tools.YtDlp));
        }
        await tools.EnsureAsync(null, default); // the next use swaps the waiting copy in
        Assert.Equal("yt-dlp 2026.11.04", File.ReadAllText(tools.YtDlp));
        Assert.Equal("2026.11.04", tools.Status().YtDlpVersion);
        var calls = web.Calls($"{Host}/api/yt-dlp");
        time.Advance(TimeSpan.FromHours(1));
        Assert.False(await tools.UpdateYtDlpAsync(default));
        Assert.Equal(calls, web.Calls($"{Host}/api/yt-dlp"));
    }

    [Fact]
    public async Task UpdateNowLooksEvenWhenTodayWasChecked()
    {
        Publish();
        var tools = Tools();
        await tools.EnsureAsync(null, default);
        Publish("2026.11.04");
        Assert.False(await tools.UpdateYtDlpAsync(default)); // the daily look waits
        Assert.True(await tools.UpdateYtDlpAsync(default, force: true));
        Assert.Equal("2026.11.04", tools.Status().YtDlpVersion);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private sealed class FakeReleases : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> files = [];
        private readonly Dictionary<string, int> calls = [];
        public int Calls(string url) { lock (calls) return calls.GetValueOrDefault(url); }

        public void Release(string api, string tag, params (string Url, byte[] Data)[] assets)
        {
            foreach (var (url, data) in assets) files[url] = data;
            files[api] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                tag_name = tag,
                assets = assets.Select(a => new { name = a.Url[(a.Url.LastIndexOf('/') + 1)..], browser_download_url = a.Url }),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (calls) calls[url] = calls.GetValueOrDefault(url) + 1;
            Assert.Contains("IDM-Rea", request.Headers.UserAgent.ToString());
            return Task.FromResult(files.TryGetValue(url, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
