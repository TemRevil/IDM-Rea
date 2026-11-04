using System.Net;
using System.Net.Sockets;
using IDMRea.Contracts;
using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class HelperTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task LimiterHoldsRateWithin10Percent()
    {
        var time = new ManualTime { AutoAdvance = true }; var limiter = new RateLimiter(time) { BytesPerSecond = MiB };
        var start = time.GetUtcNow();
        for (var sent = 0; sent < 4 * MiB; sent += 64 * 1024) await limiter.WaitAsync(64 * 1024, default);
        // One second of burst is free, so 4 MiB at 1 MiB/s takes 3 s.
        var elapsed = (time.GetUtcNow() - start).TotalSeconds;
        Assert.InRange(elapsed, 2.7, 3.3);
    }

    [Fact]
    public async Task LimiterSharedByConnections()
    {
        var time = new ManualTime { AutoAdvance = true }; var limiter = new RateLimiter(time) { BytesPerSecond = 2 * MiB };
        var start = time.GetUtcNow();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => { for (var sent = 0; sent < 2 * MiB; sent += 64 * 1024) await limiter.WaitAsync(64 * 1024, default); }));
        // 8 MiB total at 2 MiB/s, 1 s burst: about 3 s, not 0.75 s per connection.
        Assert.InRange((time.GetUtcNow() - start).TotalSeconds, 2.7, 3.3);
    }

    [Fact]
    public async Task LimiterOffIsFree()
    {
        var time = new ManualTime { AutoAdvance = true }; var limiter = new RateLimiter(time);
        var start = time.GetUtcNow();
        for (var i = 0; i < 1000; i++) await limiter.WaitAsync(MiB, default);
        Assert.Equal(start, time.GetUtcNow());
    }

    [Fact]
    public void MeterAveragesThreeSeconds()
    {
        var time = new ManualTime(); var meter = new SpeedMeter(time);
        Assert.Equal(0, meter.BytesPerSecond());
        for (var i = 0; i < 12; i++) { meter.Add(MiB / 4); time.Advance(TimeSpan.FromMilliseconds(250)); }
        Assert.InRange(meter.BytesPerSecond(), 0.9 * MiB, 1.1 * MiB);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, meter.BytesPerSecond());
    }

    [Fact]
    public void MeterIsNotInflatedAtStart()
    {
        var time = new ManualTime(); var meter = new SpeedMeter(time);
        meter.Add(MiB); time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.InRange(meter.BytesPerSecond(), 0, MiB);
    }

    public static TheoryData<Exception, ErrorKind> Errors() => new()
    {
        { new TransferException(ErrorKind.Changed, "x"), ErrorKind.Changed },
        { new HttpRequestException("x", null, HttpStatusCode.NotFound), ErrorKind.ClientError },
        { new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), ErrorKind.Server },
        { new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), ErrorKind.RateLimited },
        { new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), ErrorKind.Network },
        { new IOException("reset", new SocketException((int)SocketError.ConnectionReset)), ErrorKind.Network },
        { new HttpIOException(HttpRequestError.ResponseEnded), ErrorKind.Network },
        { new SocketException((int)SocketError.HostUnreachable), ErrorKind.Network },
        { new TimeoutException(), ErrorKind.Timeout },
        { new IOException("full", unchecked((int)0x80070070)), ErrorKind.DiskFull },
        { new IOException("full", unchecked((int)0x80070027)), ErrorKind.DiskFull },
        { new UnauthorizedAccessException(), ErrorKind.AccessDenied },
        { new IOException("odd"), ErrorKind.Other },
        { new InvalidOperationException(), ErrorKind.Other },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Classify(Exception ex, ErrorKind expected) => Assert.Equal(expected, RetryPolicy.Classify(ex));

    [Theory]
    [InlineData(ErrorKind.Network, true)][InlineData(ErrorKind.Timeout, true)][InlineData(ErrorKind.Server, true)][InlineData(ErrorKind.RateLimited, true)]
    [InlineData(ErrorKind.ClientError, false)][InlineData(ErrorKind.Changed, false)][InlineData(ErrorKind.DiskFull, false)][InlineData(ErrorKind.AccessDenied, false)][InlineData(ErrorKind.Other, false)]
    public void Retryable(ErrorKind kind, bool expected) => Assert.Equal(expected, RetryPolicy.IsRetryable(kind));

    [Fact]
    public void BackoffSchedule()
    {
        Assert.Equal(new[] { 2, 5, 15, 30, 60, 60, 60 }, Enumerable.Range(1, 7).Select(a => (int)RetryPolicy.Delay(a, null).TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(7), RetryPolicy.Delay(1, TimeSpan.FromSeconds(7)));
        Assert.Equal(TimeSpan.FromMinutes(10), RetryPolicy.Delay(1, TimeSpan.FromHours(3)));
    }
}
