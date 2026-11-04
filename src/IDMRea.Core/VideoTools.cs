using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace IDMRea.Core;

/// <summary>Where each tool's latest release is listed, and the only address prefix its files may come from.</summary>
public sealed record ToolSources(string YtDlpApi, string DenoApi, string FfmpegApi, string YtDlpReleases, string DenoReleases, string FfmpegReleases)
{
    public static ToolSources Official { get; } = new(
        "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest",
        "https://api.github.com/repos/denoland/deno/releases/latest",
        "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/latest",
        "https://github.com/yt-dlp/yt-dlp/releases/download/",
        "https://github.com/denoland/deno/releases/download/",
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/");
}

/// <summary>Setup progress across all the tools being installed, 0 to 100.</summary>
public sealed record ToolProgress(string Tool, int Percent);

public sealed record ToolStatus(bool Ready, string? YtDlpVersion, bool Installing, int Percent, string? Error);

/// <summary>
/// yt-dlp (finds a video's real addresses), Deno (yt-dlp needs it for YouTube) and FFmpeg (joins picture and sound),
/// fetched on first use from their official GitHub releases into <c>&lt;data&gt;\tools</c>. Every file is checked
/// against the SHA-256 sum published with it before it is installed, and swapped in only after the check. yt-dlp is
/// checked for a newer release at most once a day and never replaced while it runs.
/// </summary>
public sealed partial class VideoTools(string dataFolder, HttpClient http, ToolSources sources, TimeProvider time)
{
    private const string YtDlpAsset = "yt-dlp.exe", YtDlpSums = "SHA2-256SUMS";
    private const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip", DenoSum = DenoAsset + ".sha256sum";
    private const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl-shared.zip", FfmpegSums = "checksums.sha256";
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim installing = new(1);
    private readonly object gate = new();
    private int users, percent;
    private bool busy;
    private string? error;

    private string Folder => Path.Combine(dataFolder, "tools");
    public string YtDlp => Path.Combine(Folder, "yt-dlp.exe");
    public string Deno => Path.Combine(Folder, "deno", "deno.exe");
    public string Ffmpeg => Path.Combine(Folder, "ffmpeg", "ffmpeg.exe");
    private string VersionFile => Path.Combine(Folder, "yt-dlp.version");
    private string CheckedFile => Path.Combine(Folder, "yt-dlp.checked");
    private string Waiting => YtDlp + ".new";

    public bool Ready => File.Exists(YtDlp) && File.Exists(Deno) && File.Exists(Ffmpeg);

    public ToolStatus Status()
    {
        var version = File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null;
        lock (gate) return new(Ready, version, busy, percent, error);
    }

    /// <summary>Held while a tool runs: a newer yt-dlp waits until nothing holds it.</summary>
    public IDisposable Use()
    {
        lock (gate) users++;
        return new Lease(this);
    }

    private sealed class Lease(VideoTools owner) : IDisposable
    {
        private int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) lock (owner.gate) owner.users--; }
    }

    private bool InUse { get { lock (gate) return users > 0; } }

    /// <summary>Installs whatever is missing, and swaps in a waiting newer yt-dlp when nothing is using it.</summary>
    public async Task EnsureAsync(IProgress<ToolProgress>? progress, CancellationToken token)
    {
        await installing.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(Waiting) && !InUse) SwapWaiting();
            var missing = new List<(string Name, Func<Action<double>, Task> Install)>();
            if (!File.Exists(YtDlp)) missing.Add(("yt-dlp", InstallYtDlpAsync));
            if (!File.Exists(Deno)) missing.Add(("Deno", report => InstallZipAsync(sources.DenoApi, sources.DenoReleases, DenoAsset, DenoSum, Path.GetDirectoryName(Deno)!, IsDeno, report, token)));
            if (!File.Exists(Ffmpeg)) missing.Add(("FFmpeg", report => InstallZipAsync(sources.FfmpegApi, sources.FfmpegReleases, FfmpegAsset, FfmpegSums, Path.GetDirectoryName(Ffmpeg)!, IsFfmpegBin, report, token)));
            if (missing.Count == 0) return;
            lock (gate) { busy = true; error = null; percent = 0; }
            try
            {
                for (var i = 0; i < missing.Count; i++)
                {
                    var (name, install) = missing[i]; var step = i;
                    void Report(double fraction)
                    {
                        var value = (int)Math.Clamp((step + fraction) * 100 / missing.Count, 0, 100);
                        lock (gate) percent = value;
                        progress?.Report(new ToolProgress(name, value));
                    }
                    Report(0);
                    await install(Report);
                    Report(1);
                }
            }
            catch (Exception ex) { lock (gate) error = ex.Message; throw; }
            finally { lock (gate) busy = false; }

            async Task InstallYtDlpAsync(Action<double> report)
            {
                var (tag, file) = await FetchVerifiedAsync(sources.YtDlpApi, sources.YtDlpReleases, YtDlpAsset, YtDlpSums, report, token);
                File.Move(file, YtDlp, true);
                File.WriteAllText(VersionFile, tag);
                MarkChecked();
            }
        }
        finally { installing.Release(); }
    }

    /// <summary>Fetches a newer yt-dlp when the last check is a day old. True when it was swapped in; a copy fetched
    /// while yt-dlp runs waits for the next <see cref="EnsureAsync"/>.</summary>
    public async Task<bool> UpdateYtDlpAsync(CancellationToken token)
    {
        if (!File.Exists(YtDlp)) return false;
        if (File.Exists(CheckedFile) && DateTimeOffset.TryParse(File.ReadAllText(CheckedFile), out var last) && time.GetUtcNow() - last < UpdateEvery) return false;
        await installing.WaitAsync(token);
        try
        {
            MarkChecked();
            var (tag, assets) = await ReleaseAsync(sources.YtDlpApi, token);
            if (tag == Status().YtDlpVersion) return false;
            var (_, file) = await VerifiedAsync(tag, assets, sources.YtDlpReleases, YtDlpAsset, YtDlpSums, _ => { }, token);
            File.Move(file, Waiting, true);
            File.WriteAllText(Waiting + ".version", tag);
            if (InUse) return false;
            SwapWaiting();
            return true;
        }
        finally { installing.Release(); }
    }

    private void SwapWaiting()
    {
        File.Move(Waiting, YtDlp, true);
        if (File.Exists(Waiting + ".version")) File.Move(Waiting + ".version", VersionFile, true);
    }

    private void MarkChecked() => File.WriteAllText(CheckedFile, time.GetUtcNow().ToString("O"));

    private static bool IsDeno(string entry) => Path.GetFileName(entry).Equals("deno.exe", StringComparison.OrdinalIgnoreCase);
    private static bool IsFfmpegBin(string entry) => entry.Replace('\\', '/').Contains("/bin/") && Path.GetFileName(entry).Length > 0;

    private async Task InstallZipAsync(string api, string releases, string asset, string sums, string target, Func<string, bool> wanted, Action<double> report, CancellationToken token)
    {
        var (_, zip) = await FetchVerifiedAsync(api, releases, asset, sums, report, token);
        var fresh = target + ".new";
        try
        {
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            Directory.CreateDirectory(fresh);
            using (var archive = ZipFile.OpenRead(zip))
                foreach (var entry in archive.Entries.Where(e => wanted(e.FullName)))
                    entry.ExtractToFile(Path.Combine(fresh, Path.GetFileName(entry.FullName)), true); // flattened: no path from the archive is trusted
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(fresh, target);
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        }
    }

    private async Task<(string Tag, string File)> FetchVerifiedAsync(string api, string releases, string asset, string sums, Action<double> report, CancellationToken token)
    {
        var (tag, assets) = await ReleaseAsync(api, token);
        return await VerifiedAsync(tag, assets, releases, asset, sums, report, token);
    }

    private async Task<(string Tag, string File)> VerifiedAsync(string tag, IReadOnlyDictionary<string, string> assets, string releases, string asset, string sums, Action<double> report, CancellationToken token)
    {
        string Official(string name) => assets.TryGetValue(name, out var url) && url.StartsWith(releases, StringComparison.Ordinal)
            ? url : throw new InvalidDataException($"{asset} is not on its official release page, so it was not installed.");
        var fileUrl = Official(asset); var sumsUrl = Official(sums);
        var expected = ExpectedSum(await GetStringAsync(sumsUrl, token), asset)
            ?? throw new InvalidDataException($"{asset} has no published checksum, so it was not installed.");
        var file = Path.Combine(Folder, asset + ".download");
        try
        {
            using var request = Request(fileUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var body = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(file))
            {
                var buffer = new byte[81920]; long received = 0; int read;
                while ((read = await body.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    if (total > 0) report(Math.Min(0.99, (double)received / total.Value));
                }
            }
            if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{asset} did not match its published checksum, so it was not installed.");
            return (tag, file);
        }
        catch { File.Delete(file); throw; }
    }

    /// <summary>Reads both layouts in use: "hash  name" lines (yt-dlp, FFmpeg), or one file's sum on its own (Deno).</summary>
    internal static string? ExpectedSum(string text, string asset)
    {
        var lines = text.Split('\n').Select(l => SumLine().Match(l.Trim())).Where(m => m.Success).ToList();
        if (lines.Count > 0) return lines.FirstOrDefault(m => m.Groups[2].Value.Trim() == asset)?.Groups[1].Value;
        var hashes = Hex64().Matches(text);
        return hashes.Count == 1 ? hashes[0].Value : null;
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")] private static partial Regex SumLine();
    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")] private static partial Regex Hex64();

    private async Task<(string Tag, IReadOnlyDictionary<string, string> Assets)> ReleaseAsync(string api, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync(api, token));
        var root = doc.RootElement;
        var assets = root.GetProperty("assets").EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("browser_download_url").GetString()!);
        return (root.GetProperty("tag_name").GetString()!, assets);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken token)
    {
        using var request = Request(url);
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("IDM-Rea", "1.0"));
        return request;
    }
}
