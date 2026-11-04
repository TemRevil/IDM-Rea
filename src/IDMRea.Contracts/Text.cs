using System.Globalization;

namespace IDMRea.Contracts;

/// <summary>Every piece of status text the app shows. Numbers always use ASCII digits.</summary>
public static class Text
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        if (bytes < 1024) return Math.Max(0, bytes).ToString(Invariant) + " B";
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(value < 100 ? "0.0" : "0", Invariant) + " " + Units[unit];
    }

    public static string Speed(long bytesPerSecond) => Size(bytesPerSecond) + "/s";

    public static string TimeLeft(TimeSpan left)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        if (seconds < 60) return seconds.ToString(Invariant) + " s left";
        var minutes = (seconds + 59) / 60;
        if (minutes < 60) return minutes.ToString(Invariant) + " min left";
        var h = minutes / 60; var m = minutes % 60;
        return m == 0 ? $"{h.ToString(Invariant)} h left" : $"{h.ToString(Invariant)} h {m.ToString(Invariant)} min left";
    }

    private static string Amount(DownloadJob job) =>
        job.TotalBytes is long total ? $"{Size(job.ReceivedBytes)} of {Size(total)}" : Size(job.ReceivedBytes);

    public static string StatusLine(DownloadJob job, long bytesPerSecond, DateTimeOffset now, int retryAttempts = 5) => job.Status switch
    {
        DownloadStatus.Downloading when bytesPerSecond <= 0 => Amount(job),
        DownloadStatus.Downloading when job.TotalBytes is long total =>
            $"{Amount(job)} - {Speed(bytesPerSecond)} - {TimeLeft(TimeSpan.FromSeconds((double)Math.Max(0, total - job.ReceivedBytes) / bytesPerSecond))}",
        DownloadStatus.Downloading => $"{Amount(job)} - {Speed(bytesPerSecond)}",
        DownloadStatus.Queued => "Waiting",
        DownloadStatus.Paused => $"Paused - {Amount(job)}",
        DownloadStatus.Retrying =>
            $"Retrying in {Math.Max(0, (long)Math.Ceiling(((job.NextRetryAt ?? now) - now).TotalSeconds)).ToString(Invariant)} s (attempt {job.Attempts.ToString(Invariant)} of {retryAttempts.ToString(Invariant)})",
        DownloadStatus.Finalizing => "Checking file",
        DownloadStatus.Completed => $"Done - {Size(job.TotalBytes ?? job.ReceivedBytes)}",
        DownloadStatus.Failed => "Failed - " + LowerFirst(Describe(job.ErrorKind == ErrorKind.None ? ErrorKind.Other : job.ErrorKind, job.HttpStatus)),
        DownloadStatus.Cancelled => "Cancelled",
        _ => job.Status.ToString(),
    };

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    public static string Describe(ErrorKind kind, int? httpStatus) => kind switch
    {
        ErrorKind.Network => "Can't reach the server. Check your connection.",
        ErrorKind.Timeout => "The server stopped responding.",
        ErrorKind.Server => httpStatus is int s ? $"The server had a problem (HTTP {s.ToString(Invariant)})." : "The server had a problem.",
        ErrorKind.RateLimited => "The server asked to slow down.",
        ErrorKind.ClientError => httpStatus switch
        {
            404 or 410 => "The server no longer has this file. Check the link.",
            401 or 403 or null => "The server refused the request.",
            int s => $"The server refused the request (HTTP {s.ToString(Invariant)}).",
        },
        ErrorKind.Changed => "The file changed on the server. Retry to download it again from the start.",
        ErrorKind.DiskFull => "Not enough disk space.",
        ErrorKind.AccessDenied => "Can't write to the save folder.",
        _ => "Something went wrong.",
    };

    /// <summary>Converts Arabic-Indic and Extended Arabic-Indic digits to 0-9.</summary>
    public static string NormalizeDigits(string s) => string.Create(s.Length, s, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠'))
                : c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c;
        }
    });
}
