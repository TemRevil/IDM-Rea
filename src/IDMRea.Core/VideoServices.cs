using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>Fresh stream addresses for one choice of a page (yt-dlp, in the engine).</summary>
public interface IVideoStreams
{
    Task<ResolvedStreams> StreamsAsync(string pageUrl, string choiceId, RequestHeaders? headers, CancellationToken token);
}

/// <summary>Puts a video's streams into one file of the given container (FFmpeg, in the engine).</summary>
public interface IVideoJoiner
{
    Task JoinAsync(IReadOnlyList<string> inputs, string output, string container, CancellationToken token);
}

public sealed record VideoServices(IVideoStreams Streams, IVideoJoiner Joiner);
