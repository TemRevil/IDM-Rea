using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>
/// The engine's video side: the tools, yt-dlp to find a page's video, and FFmpeg to join it. Tools are set up in the
/// background on first use; until they are ready, questions are answered with the setup's progress.
/// </summary>
public sealed class VideoSupport(VideoTools tools, IToolRunner runner, string tempFolder, HttpClient http) : IVideoStreams, IVideoJoiner
{
    /// <summary>The choice for a plain video file the page played: it downloads like any other link.</summary>
    public const string FileChoice = "file";
    private const int MostMediaTried = 5;

    private readonly VideoResolver resolver = new(runner, tempFolder, tools.YtDlp, tools.Deno);
    private readonly Ffmpeg ffmpeg = new(runner, tools.Ffmpeg);
    private readonly object gate = new();
    private Task? setup;

    public ToolStatus Status()
    {
        var status = tools.Status();
        lock (gate) return setup is { IsCompleted: false } && !status.Ready ? status with { Installing = true } : status;
    }

    /// <summary>Starts setting up whatever tool is missing, unless that is already under way, and says how far it got.
    /// A setup that failed is reported once, then the next question tries again.</summary>
    public ToolStatus Prepare()
    {
        lock (gate)
        {
            if (tools.Ready) return tools.Status();
            if (setup is { IsFaulted: true } failed)
            {
                setup = null;
                return tools.Status() with { Installing = false, Error = tools.Status().Error ?? failed.Exception?.InnerException?.Message };
            }
            if (setup is null or { IsCompleted: true }) setup = Task.Run(() => tools.EnsureAsync(null, CancellationToken.None));
        }
        return tools.Status() with { Installing = true };
    }

    /// <summary>Settings' button: tools that are missing start setting up (in the background, as on first use);
    /// installed tools get the newest yt-dlp now, whenever it was last checked.</summary>
    public async Task<ToolStatus> UpdateAsync(CancellationToken token)
    {
        if (!tools.Ready) return Prepare();
        await tools.EnsureAsync(null, token);
        await tools.UpdateYtDlpAsync(token, force: true);
        return tools.Status();
    }

    /// <summary>
    /// What a page plays. A page yt-dlp doesn't know is tried through the media addresses the browser saw it load,
    /// first with yt-dlp (HLS lists and the like), then as plain files.
    /// </summary>
    /// <exception cref="VideoException">No video was found, or the site refused (its words).</exception>
    public async Task<VideoInfo> ProbeAsync(string url, RequestHeaders? headers, IReadOnlyList<string>? media, CancellationToken token)
    {
        Ready();
        _ = UpdateQuietlyAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(Protocol.VideoLimit);
        using (tools.Use())
        {
            try { return await resolver.ResolveAsync(url, headers, limit.Token); }
            catch (VideoException ex) when (ex.Unsupported && media is { Count: > 0 }) { }
            var seen = media!.Where(m => Uri.TryCreate(m, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").Distinct().Take(MostMediaTried).ToArray();
            foreach (var address in seen)
            {
                try { return await resolver.ResolveAsync(address, headers, limit.Token); }
                catch (VideoException ex) when (ex.Unsupported) { }
            }
            foreach (var address in seen)
            {
                ProbeResult probe;
                try { probe = await Prober.ProbeAsync(http, address, limit.Token, headers); }
                catch (Exception) when (!limit.IsCancellationRequested) { continue; }
                if (probe.Category is not (Category.Video or Category.Music)) continue;
                var extension = Path.GetExtension(probe.FileName).TrimStart('.').ToLowerInvariant();
                var label = extension.ToUpperInvariant() + (probe.TotalBytes is long bytes ? ", " + Text.Size(bytes) : "");
                return new VideoInfo(address, Path.GetFileNameWithoutExtension(probe.FileName), null, null,
                    [new VideoChoice(FileChoice, label, extension, probe.TotalBytes, null, probe.Category == Category.Music)]);
            }
        }
        throw new VideoException("IDM-Rea found no video on this page.", unsupported: true);
    }

    public async Task<ResolvedStreams> StreamsAsync(string pageUrl, string choiceId, RequestHeaders? headers, CancellationToken token)
    {
        Ready();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(Protocol.VideoLimit);
        using (tools.Use()) return await resolver.StreamsAsync(pageUrl, choiceId, headers, limit.Token);
    }

    public async Task JoinAsync(IReadOnlyList<string> inputs, string output, string container, CancellationToken token)
    {
        Ready();
        using (tools.Use()) await ffmpeg.JoinAsync(inputs, output, container, token);
    }

    private void Ready()
    {
        if (!tools.Ready) throw new VideoException("IDM-Rea is still getting its video tools ready. Try again in a moment.");
    }

    /// <summary>The daily look for a newer yt-dlp. A failed look is tried again tomorrow; the current one keeps working.</summary>
    private async Task UpdateQuietlyAsync()
    {
        try { await tools.UpdateYtDlpAsync(CancellationToken.None); }
        catch (Exception) { }
    }
}
