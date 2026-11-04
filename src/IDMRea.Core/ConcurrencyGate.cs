namespace IDMRea.Core;

/// <summary>
/// "Downloads at once": at most <see cref="Limit"/> holders, first come first served. Raising the limit lets
/// waiters in at once; lowering it lets current holders finish and admits no one until there is room.
/// </summary>
public sealed class ConcurrencyGate(int limit)
{
    private readonly object gate = new();
    private readonly LinkedList<TaskCompletionSource> waiting = new();
    private int limit = Math.Max(1, limit), running;

    public int Limit
    {
        get { lock (gate) return limit; }
        set { List<TaskCompletionSource> wake; lock (gate) { limit = Math.Max(1, value); wake = Admit(); } wake.ForEach(w => w.TrySetResult()); }
    }

    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TaskCompletionSource ticket; LinkedListNode<TaskCompletionSource> node;
        lock (gate)
        {
            if (running < limit && waiting.Count == 0) { running++; return new Lease(this); }
            ticket = new(TaskCreationOptions.RunContinuationsAsynchronously); node = waiting.AddLast(ticket);
        }
        await using (token.Register(() =>
        {
            bool removed; lock (gate) { removed = node.List != null; if (removed) waiting.Remove(node); }
            if (removed) ticket.TrySetCanceled(token);
        }))
            await ticket.Task;
        return new Lease(this);
    }

    private List<TaskCompletionSource> Admit()
    {
        var admitted = new List<TaskCompletionSource>();
        while (running < limit && waiting.First is { } first) { waiting.RemoveFirst(); running++; admitted.Add(first.Value); }
        return admitted;
    }

    private void Release() { List<TaskCompletionSource> wake; lock (gate) { running--; wake = Admit(); } wake.ForEach(w => w.TrySetResult()); }

    private sealed class Lease(ConcurrencyGate owner) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(); }
    }
}
