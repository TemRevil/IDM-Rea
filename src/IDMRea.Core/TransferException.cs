using System.Net;
using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>A download error with a known cause. It derives from <see cref="IOException"/> so existing I/O handling still applies.</summary>
public sealed class TransferException(ErrorKind kind, string message, int? httpStatus = null, TimeSpan? retryAfter = null) : IOException(message)
{
    public ErrorKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static ErrorKind KindForStatus(int status) => status switch
    {
        408 => ErrorKind.Timeout,
        429 => ErrorKind.RateLimited,
        >= 500 => ErrorKind.Server,
        >= 400 => ErrorKind.ClientError,
        _ => ErrorKind.Other,
    };

    public static TransferException FromResponse(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        TimeSpan? after = response.Headers.RetryAfter switch
        {
            { Delta: TimeSpan delta } => delta,
            { Date: DateTimeOffset date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        if (after < TimeSpan.Zero) after = TimeSpan.Zero;
        return new(KindForStatus(status), $"HTTP {status} {response.ReasonPhrase}".Trim(), status, after);
    }

    public static bool IsSuccess(HttpStatusCode code) => (int)code is >= 200 and < 300;
}
