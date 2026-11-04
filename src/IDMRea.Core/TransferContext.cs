using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>What a transfer needs from its manager. <paramref name="Checkpoint"/> saves a durable copy of the job.
/// <paramref name="WriteFault"/> is a test seam called before each write with its file offset; an exception it returns is thrown.
/// <paramref name="FreeSpace"/> replaces the drive's free bytes (test seam). <paramref name="Headers"/> go on every request.</summary>
public sealed record TransferContext(HttpClient Http, RateLimiter Limiter, SpeedMeter Meter, Func<EngineSettings> Settings,
    Action<DownloadJob> Checkpoint, Func<long, Exception?>? WriteFault = null, Func<string, long>? FreeSpace = null, RequestHeaders? Headers = null)
{
    internal void BeforeWrite(long offset) { if (WriteFault?.Invoke(offset) is { } fault) throw fault; }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>, or null when the drive cannot say (a network share).</summary>
    internal long? FreeBytes(string path)
    {
        try { return FreeSpace?.Invoke(path) ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
