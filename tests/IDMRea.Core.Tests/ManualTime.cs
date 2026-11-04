namespace IDMRea.Tests;

/// <summary>
/// A fake clock. With <see cref="AutoAdvance"/> on, every timer fires at once and moves the clock forward by its due time,
/// so code that waits finishes instantly while the clock shows how long it would have waited.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly List<FakeTimer> timers = [];
    public bool AutoAdvance { get; init; }
    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return now.UtcTicks; }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (gate) { now += by; due = timers.Where(t => t.Due <= now).ToList(); }
        foreach (var t in due) t.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state); timer.Change(dueTime, period); return timer;
    }

    private sealed class FakeTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due = DateTimeOffset.MaxValue;
        private TimeSpan period = Timeout.InfiniteTimeSpan;
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            period = newPeriod;
            lock (owner.gate)
            {
                owner.timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) { Due = DateTimeOffset.MaxValue; return true; }
                Due = owner.now + dueTime; owner.timers.Add(this);
            }
            if (owner.AutoAdvance) ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (owner.gate) { if (Due == DateTimeOffset.MaxValue) return; if (Due > owner.now) owner.now = Due; }
                Fire();
            });
            else if (dueTime <= TimeSpan.Zero) Fire();
            return true;
        }
        public void Fire()
        {
            lock (owner.gate) { if (!owner.timers.Remove(this)) return; Due = DateTimeOffset.MaxValue; }
            callback(state);
            if (period != Timeout.InfiniteTimeSpan && period > TimeSpan.Zero) Change(period, period);
        }
        public void Dispose() { lock (owner.gate) { owner.timers.Remove(this); Due = DateTimeOffset.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
