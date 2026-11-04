using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>What a transfer needs from its manager. <paramref name="Checkpoint"/> saves a durable copy of the job.
/// <paramref name="WriteFault"/> is a test seam called before each write with its file offset; an exception it returns is thrown.</summary>
public sealed record TransferContext(HttpClient Http, RateLimiter Limiter, SpeedMeter Meter, Func<EngineSettings> Settings,
    Action<DownloadJob> Checkpoint, Func<long, Exception?>? WriteFault = null)
{
    internal void BeforeWrite(long offset) { if (WriteFault?.Invoke(offset) is { } fault) throw fault; }
}
