namespace IDMRea.Core;

/// <summary>
/// Joins a video's streams with FFmpeg by copying them (no re-encoding, so it takes seconds and loses nothing). Every
/// picture and sound stream of every input goes in; subtitles and data streams are left out.
/// </summary>
public sealed class Ffmpeg(IToolRunner runner, string exe) : IVideoJoiner
{
    public async Task JoinAsync(IReadOnlyList<string> inputs, string output, string container, CancellationToken token)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        foreach (var input in inputs) args.AddRange(["-i", input]);
        for (var i = 0; i < inputs.Count; i++) args.AddRange(["-map", $"{i}:v?", "-map", $"{i}:a?"]);
        args.AddRange(["-c", "copy", "-f", Muxer(container), output]);
        var run = await runner.RunAsync(exe, args, token);
        if (run.ExitCode != 0) throw new IOException("FFmpeg couldn't join the video: " + Reason(run.Errors));
    }

    private static string Muxer(string container) => container switch
    {
        "mp4" or "m4a" => "mp4",
        "webm" => "webm",
        _ => "matroska",
    };

    /// <summary>FFmpeg's first error line, without its "[muxer @ address]" prefix.</summary>
    private static string Reason(string errors)
    {
        var line = errors.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "it stopped without saying why.";
        return line.StartsWith('[') && line.IndexOf("] ", StringComparison.Ordinal) is > 0 and var end ? line[(end + 2)..] : line;
    }
}
