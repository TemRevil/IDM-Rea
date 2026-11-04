namespace IDMRea.Contracts;

/// <summary>One quality a person can pick, for example "1080p MP4, 255 MB". <see cref="Id"/> is yt-dlp's format
/// selector ("299+140" for picture plus sound, "140" for sound alone).</summary>
public sealed record VideoChoice(string Id, string Label, string Container, long? Bytes, int? Height, bool AudioOnly);

/// <summary>What a page offers: its title and the qualities, best first.</summary>
public sealed record VideoInfo(string PageUrl, string Title, double? Duration, string? Thumbnail, IReadOnlyList<VideoChoice> Choices);

/// <summary>
/// One stream of a video job: the picture, the sound, or one file holding both. <see cref="Url"/> can expire; the job
/// then asks yt-dlp again for the same <see cref="FormatId"/>. Requests never ask for more than <see cref="ChunkSize"/>.
/// </summary>
public sealed record VideoStream(string FormatId, string Url, string Protocol, string Extension,
    IReadOnlyDictionary<string, string>? Headers = null, long? TotalBytes = null, long? ChunkSize = null,
    string PartialPath = "", long ReceivedBytes = 0, IReadOnlyList<DownloadPart>? Parts = null, bool Done = false,
    int SegmentsDone = 0);

public enum VideoStage { Downloading, Joining }

/// <summary>The video tools (yt-dlp, Deno, FFmpeg): all installed, or being installed (0 to 100), or why setup failed.</summary>
public sealed record ToolStatus(bool Ready, string? YtDlpVersion, bool Installing, int Percent, string? Error);

/// <summary>A download of what a page plays: its streams are fetched by the engine, then joined into one file.</summary>
public sealed record VideoJob(string PageUrl, string ChoiceId, string Container, IReadOnlyList<VideoStream> Streams,
    VideoStage Stage = VideoStage.Downloading, string? Title = null, string? Quality = null);

public static class Videos
{
    private const int LongestTitle = 150;

    /// <summary>The file a choice is saved as: the title, made safe, plus the quality and type ("Clip (1080p).mp4").
    /// A long title is cut; the quality and the type always stay.</summary>
    public static string FileName(string title, VideoChoice choice)
    {
        title = FileNames.SafeName(Text.NormalizeDigits(title.Trim()));
        if (title is "" or "download") title = "Video";
        if (title.Length > LongestTitle) title = title[..LongestTitle].TrimEnd(' ', '.');
        var quality = choice is { AudioOnly: false, Height: int height } ? $" ({height}p)" : "";
        return title + quality + "." + choice.Container;
    }

    /// <summary>"1080p MP4", or "Sound only M4A".</summary>
    public static string Quality(VideoChoice choice) =>
        (choice.AudioOnly ? "Sound only" : choice.Height is int height ? $"{height}p" : "Video") + " " + choice.Container.ToUpperInvariant();
}
