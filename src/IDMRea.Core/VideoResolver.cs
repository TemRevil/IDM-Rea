using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IDMRea.Contracts;
namespace IDMRea.Core;

public sealed record ToolRun(int ExitCode, string Output, string Errors);

/// <summary>Runs a tool with its arguments passed one by one (never through a shell). Tests replace it.</summary>
public interface IToolRunner { Task<ToolRun> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token); }

public sealed class ProcessRunner : IToolRunner
{
    public async Task<ToolRun> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"{Path.GetFileName(exe)} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        return new ToolRun(process.ExitCode, await output, await errors);
    }
}

/// <summary>A video that can't be resolved, in words a person can act on.</summary>
public sealed class VideoException(string message, bool unsupported = false) : Exception(message) { public bool Unsupported => unsupported; }

public sealed record ResolvedStreams(VideoInfo Info, VideoChoice Choice, IReadOnlyList<VideoStream> Streams);

/// <summary>
/// Asks yt-dlp what a page plays and turns its formats into a short list of choices: one per height, the most
/// compatible picture (AVC, then AV1, then VP9) with the sound that fits its container, plus sound alone.
/// </summary>
public sealed partial class VideoResolver(IToolRunner runner, string tempFolder, string ytDlp, string deno)
{
    private sealed record Format(string Id, string Ext, string Protocol, string? VCodec, string? ACodec, int? Height, double Rate,
        long? Size, string Url, IReadOnlyDictionary<string, string>? Headers, long? Chunk, bool Drm)
    {
        public bool HasVideo => VCodec is { } v && v != "none";
        public bool HasAudio => ACodec is { } a && a != "none";
        public int ProtocolRank => Protocol is "https" or "http" ? 0 : 1;
        public int CodecRank => VCodec switch
        {
            { } v when v.StartsWith("avc1") => 0, { } v when v.StartsWith("av01") => 1,
            { } v when v.StartsWith("vp9") || v.StartsWith("vp09") => 2, _ => 3,
        };
        public bool Mp4Family => VCodec is { } v && (v.StartsWith("avc1") || v.StartsWith("av01"));
        public bool Reduced => Id.EndsWith("-drc", StringComparison.Ordinal);
    }

    public async Task<VideoInfo> ResolveAsync(string url, RequestHeaders? headers, CancellationToken token) => (await ReadAsync(url, headers, token)).Info;

    /// <summary>Fresh addresses for one choice (they expire, so they are always asked for again).</summary>
    public async Task<ResolvedStreams> StreamsAsync(string url, string choiceId, RequestHeaders? headers, CancellationToken token)
    {
        var (info, formats) = await ReadAsync(url, headers, token);
        var byId = formats.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var picked = choiceId.Split('+').Select(id => byId.TryGetValue(id, out var f) ? f : throw new VideoException("That quality is no longer offered for this video.")).ToList();
        var choice = info.Choices.FirstOrDefault(c => c.Id == choiceId) ?? Describe(picked);
        var streams = picked.Select(f => new VideoStream(f.Id, f.Url, f.Protocol, f.Ext, f.Headers, f.Size, f.Chunk)).ToList();
        return new ResolvedStreams(info, choice, streams);
    }

    private async Task<(VideoInfo Info, IReadOnlyList<Format> Formats)> ReadAsync(string url, RequestHeaders? headers, CancellationToken token)
    {
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--js-runtimes", "deno:" + deno };
        string? cookies = null;
        try
        {
            if (!string.IsNullOrEmpty(headers?.Cookies))
            {
                Directory.CreateDirectory(tempFolder);
                cookies = Path.Combine(tempFolder, $"cookies-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(cookies, CookieFile(url, headers.Cookies), token);
                args.AddRange(["--cookies", cookies]);
            }
            if (!string.IsNullOrEmpty(headers?.UserAgent)) args.AddRange(["--user-agent", headers.UserAgent]);
            if (!string.IsNullOrEmpty(headers?.Referrer)) args.AddRange(["--referer", headers.Referrer]);
            args.AddRange(["--", url]);
            var run = await runner.RunAsync(ytDlp, args, token);
            if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.Output)) throw Failure(run.Errors);
            return Parse(url, run.Output);
        }
        finally { if (cookies != null) File.Delete(cookies); }
    }

    /// <summary>The browser's cookies for the page, in the Netscape layout yt-dlp reads, for the site and its subdomains.</summary>
    private static string CookieFile(string url, string header)
    {
        var uri = new Uri(url);
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var secure = uri.Scheme == "https" ? "TRUE" : "FALSE";
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var pair in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            text.Append('.').Append(host).Append("\tTRUE\t/\t").Append(secure).Append("\t0\t").Append(pair[..eq]).Append('\t').Append(pair[(eq + 1)..]).Append('\n');
        }
        return text.ToString();
    }

    private static VideoException Failure(string errors)
    {
        var line = errors.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line == null) return new VideoException("yt-dlp could not read this page.");
        if (line.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return new VideoException("IDM-Rea found no video on this page.", true);
        if (line.Contains("DRM", StringComparison.Ordinal)) return Protected();
        var text = ErrorText().Match(line).Groups[2].Value.Trim();
        return new VideoException(text.Length > 0 ? text : "yt-dlp could not read this page.");
    }

    private static VideoException Protected() => new("This video is protected against copying (DRM), so it can't be downloaded.");

    [GeneratedRegex(@"^ERROR:\s*(\[[^\]]+\]\s*[^:\s]+:\s*)?(.*)$")] private static partial Regex ErrorText();

    private static (VideoInfo, IReadOnlyList<Format>) Parse(string url, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            root = entries.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formats", out var list)) throw new VideoException("IDM-Rea found no video on this page.", true);

        var all = list.EnumerateArray().Select(Read).OfType<Format>().Where(f => f.Protocol != "mhtml" && (f.HasVideo || f.HasAudio)).ToList();
        if (all.Count > 0 && all.All(f => f.Drm)) throw Protected();
        var formats = all.Where(f => !f.Drm && f.Protocol is "https" or "http" or "m3u8_native" or "m3u8" or "http_dash_segments").ToList();
        if (formats.Count == 0) throw new VideoException("IDM-Rea found no video on this page.", true);

        var choices = new List<VideoChoice>();
        var audio = formats.Where(f => f.HasAudio && !f.HasVideo).ToList();
        foreach (var group in formats.Where(f => f.HasVideo && f.Height > 0).GroupBy(f => f.Height!.Value).OrderByDescending(g => g.Key))
        {
            var pictures = group.Where(f => !f.HasAudio).ToList();
            if (pictures.Count > 0 && audio.Count > 0)
            {
                var picture = pictures.OrderBy(f => f.ProtocolRank).ThenBy(f => f.CodecRank).ThenByDescending(f => f.Rate).First();
                choices.Add(Describe([picture, SoundFor(picture, audio)]));
            }
            else if (group.Where(f => f.HasAudio).OrderBy(f => f.ProtocolRank).ThenBy(f => f.CodecRank).ThenByDescending(f => f.Rate).FirstOrDefault() is { } both)
                choices.Add(Describe([both]));
        }
        var m4a = audio.Where(f => f.Ext == "m4a").ToList();
        if (audio.Count > 0) choices.Add(Describe([Best(m4a.Count > 0 ? m4a : audio)]));

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
        var thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;
        return (new VideoInfo(url, title, duration, thumbnail, choices), formats);
    }

    private static Format Best(IEnumerable<Format> audio) =>
        audio.OrderBy(f => f.Reduced).ThenBy(f => f.ProtocolRank).ThenByDescending(f => f.Rate).First();

    /// <summary>AAC sound for an MP4 picture, Opus for VP9; anything else when that is all there is.</summary>
    private static Format SoundFor(Format picture, List<Format> audio)
    {
        var fitting = audio.Where(a => picture.Mp4Family ? a.ACodec!.StartsWith("mp4a") : a.ACodec == "opus").ToList();
        return Best(fitting.Count > 0 ? fitting : audio);
    }

    private static VideoChoice Describe(IReadOnlyList<Format> picks)
    {
        var id = string.Join('+', picks.Select(p => p.Id));
        long? bytes = picks.All(p => p.Size != null) ? picks.Sum(p => p.Size!.Value) : null;
        var size = bytes is long b ? ", " + Text.Size(b) : "";
        if (picks.Count == 1 && !picks[0].HasVideo)
            return new VideoChoice(id, $"Sound only {picks[0].Ext.ToUpperInvariant()}{size}", picks[0].Ext, bytes, null, true);
        var container = picks.Count == 1 ? picks[0].Ext
            : picks[0].Ext == "mp4" && picks[1].Ext == "m4a" ? "mp4"
            : picks[0].Ext == "webm" && picks[1].Ext == "webm" ? "webm" : "mkv";
        return new VideoChoice(id, $"{picks[0].Height}p {container.ToUpperInvariant()}{size}", container, bytes, picks[0].Height, false);
    }

    private static Format? Read(JsonElement f)
    {
        string? Str(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var id = Str("format_id"); var url = Str("url");
        if (id == null || url == null) return null;
        IReadOnlyDictionary<string, string>? headers = null;
        if (f.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
            headers = h.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
        long? chunk = f.TryGetProperty("downloader_options", out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("http_chunk_size", out var c)
            && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : null;
        var size = Num("filesize") ?? Num("filesize_approx");
        return new Format(id, Str("ext") ?? "mp4", Str("protocol") ?? "https", Str("vcodec"), Str("acodec"), (int?)Num("height"),
            Num("tbr") ?? Num("abr") ?? 0, size is double s ? (long)s : null, url, headers, chunk,
            f.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True);
    }
}
