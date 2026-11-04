namespace IDMRea.Engine.Windows;

/// <summary>Gives memory back to Windows once downloads have been quiet for 10 s. Never runs while anything downloads.</summary>
public sealed class MemoryTrim : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly Timer timer;
    private bool busy = true;

    public MemoryTrim() => timer = new Timer(_ => Trim(), null, Timeout.Infinite, Timeout.Infinite);

    public void Update(bool active)
    {
        lock (gate)
        {
            if (active == busy) return;
            busy = active;
            timer.Change(active ? Timeout.InfiniteTimeSpan : Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Trim()
    {
        lock (gate) if (busy) return;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
    }

    public void Dispose() => timer.Dispose();
}
