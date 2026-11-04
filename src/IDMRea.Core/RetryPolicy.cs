using System.Net.Sockets;
using IDMRea.Contracts;
namespace IDMRea.Core;

public static class RetryPolicy
{
    private static readonly int[] Schedule = [2, 5, 15, 30, 60];

    public static ErrorKind Classify(Exception ex) => ex switch
    {
        TransferException t => t.Kind,
        HttpRequestException { StatusCode: { } status } => TransferException.KindForStatus((int)status),
        HttpRequestException => ErrorKind.Network,
        HttpIOException => ErrorKind.Network,
        SocketException => ErrorKind.Network,
        TimeoutException or OperationCanceledException => ErrorKind.Timeout,
        UnauthorizedAccessException => ErrorKind.AccessDenied,
        IOException io when IsDiskFull(io) => ErrorKind.DiskFull,
        IOException { InnerException: SocketException } => ErrorKind.Network,
        IOException { InnerException: { } inner } when Classify(inner) is ErrorKind.Network or ErrorKind.Timeout => ErrorKind.Network,
        _ => ErrorKind.Other,
    };

    private static bool IsDiskFull(IOException io)
    {
        var code = io.HResult;
        return code == unchecked((int)0x80070070) || code == unchecked((int)0x80070027) || (!OperatingSystem.IsWindows() && code == 28);
    }

    public static bool IsRetryable(ErrorKind kind) => kind is ErrorKind.Network or ErrorKind.Timeout or ErrorKind.Server or ErrorKind.RateLimited;

    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is TimeSpan after) return after > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : after;
        return TimeSpan.FromSeconds(Schedule[Math.Clamp(attempt, 1, Schedule.Length) - 1]);
    }
}
