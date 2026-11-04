using System.Text.Json.Serialization;

namespace IDMRea.Contracts;

public enum DownloadStatus { Queued, Downloading, Paused, Finalizing, Completed, Failed, Cancelled, Retrying }

/// <summary>Who paused a job: the person (stays paused on restart) or the engine shutting down (continues on restart).</summary>
public enum PausedBy { None, Person, Engine }

public enum ErrorKind { None, Network, Timeout, Server, RateLimited, ClientError, Changed, DiskFull, AccessDenied, Other, NameTaken, Lost }

/// <summary>One byte range of a multi-part download. <see cref="End"/> is inclusive; <see cref="Received"/> counts from <see cref="Start"/>.</summary>
public sealed record DownloadPart(long Start, long End, long Received)
{
    [JsonIgnore] public long Length => End - Start + 1;
    [JsonIgnore] public long Remaining => Length - Received;
    [JsonIgnore] public bool Done => Received >= Length;
}

public sealed record DownloadJob(Guid Id, string Url, string Destination, string PartialPath,
    DownloadStatus Status, long ReceivedBytes = 0, long? TotalBytes = null, string? ETag = null,
    DateTimeOffset? LastModified = null, string? Error = null, DateTimeOffset UpdatedAt = default,
    string? CompletionFileId = null,
    Category Category = Category.Other, DateTimeOffset CreatedAt = default, DateTimeOffset? CompletedAt = null,
    int Attempts = 0, DateTimeOffset? NextRetryAt = null, ErrorKind ErrorKind = ErrorKind.None, int? HttpStatus = null,
    IReadOnlyList<DownloadPart>? Parts = null, PausedBy PausedBy = PausedBy.None, bool Resumable = false,
    string? ResolvedUrl = null, string? Referrer = null, string? UserAgent = null, string? ProtectedCookies = null)
{
    [JsonIgnore] public string FileName => Path.GetFileName(Destination);
}
