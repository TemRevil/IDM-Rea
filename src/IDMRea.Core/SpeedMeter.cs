namespace IDMRea.Core;

/// <summary>Bytes per second over the last 3 s, kept in twelve 250 ms slots so memory stays constant.</summary>
public sealed class SpeedMeter(TimeProvider? time = null)
{
    private const int Slots = 12;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly long[] bytes = new long[Slots];
    private readonly long[] stamps = Enumerable.Repeat(long.MinValue, Slots).ToArray();
    private long firstSlot = long.MinValue;

    private long SlotNow() => clock.GetTimestamp() / (clock.TimestampFrequency / 4);

    public void Add(long count)
    {
        lock (gate)
        {
            var slot = SlotNow(); var i = (int)(slot % Slots);
            if (stamps[i] != slot) { stamps[i] = slot; bytes[i] = 0; }
            bytes[i] += count;
            if (firstSlot == long.MinValue) firstSlot = slot;
        }
    }

    public long BytesPerSecond()
    {
        lock (gate)
        {
            if (firstSlot == long.MinValue) return 0;
            var slot = SlotNow(); long sum = 0;
            for (var i = 0; i < Slots; i++) if (stamps[i] >= slot - Slots && stamps[i] < slot) sum += bytes[i];
            var covered = Math.Clamp(slot - firstSlot, 1, Slots);
            return (long)(sum / (covered * 0.25));
        }
    }
}
