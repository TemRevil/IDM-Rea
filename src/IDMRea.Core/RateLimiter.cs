namespace IDMRea.Core;

/// <summary>One token bucket shared by every connection. 0 means unlimited. Burst is one second's worth.</summary>
public sealed class RateLimiter(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private double tokens;
    private long last, rate;

    public long BytesPerSecond
    {
        get { lock (gate) return rate; }
        set
        {
            lock (gate)
            {
                var wasOff = rate <= 0; Refill();
                rate = Math.Max(0, value);
                tokens = wasOff ? rate : Math.Min(tokens, rate);
                last = clock.GetTimestamp();
            }
        }
    }

    private void Refill()
    {
        var now = clock.GetTimestamp();
        if (rate > 0) tokens = Math.Min(rate, tokens + (double)(now - last) / clock.TimestampFrequency * rate);
        last = now;
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken token)
    {
        TimeSpan wait;
        lock (gate)
        {
            if (rate <= 0) return;
            Refill(); tokens -= bytes;
            if (tokens >= 0) return;
            wait = TimeSpan.FromSeconds(-tokens / rate);
        }
        await Task.Delay(wait, clock, token);
    }
}
