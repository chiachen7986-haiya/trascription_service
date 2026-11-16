namespace BlueJays.Core.Conversation;

/// <summary>Replaces queued drafts by key without dropping other utterances. Final work stays FIFO.</summary>
public sealed class CoalescingQueue<T>(int capacity = 64) where T : notnull
{
    private readonly object gate = new();
    private readonly LinkedList<(string Key, T Value)> items = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> index = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private bool completed;

    public bool Put(string key, T value)
    {
        lock (gate)
        {
            if (completed) return false;
            if (index.TryGetValue(key, out var node)) node.Value = (key, value);
            else
            {
                if (items.Count >= capacity) return false;
                index[key] = items.AddLast((key, value));
            }
            Signal(); return true;
        }
    }

    public void Complete() { lock (gate) { completed = true; Signal(); } }
    private void Signal() { if (changed.CurrentCount == 0) changed.Release(); }

    public async Task<IReadOnlyList<T>> TakeAsync(int maximum, CancellationToken token, Func<T, int>? size = null, int maximumSize = int.MaxValue)
    {
        if (maximum < 1 || maximumSize < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        while (true)
        {
            lock (gate)
            {
                if (items.Count > 0)
                {
                    var result = new List<T>(); long totalSize = 0;
                    while (items.First is { } first && result.Count < maximum)
                    {
                        var itemSize = Math.Max(0, size?.Invoke(first.Value.Value) ?? 0);
                        // Never wait for a batch and never block an oversized first item.
                        if (result.Count > 0 && totalSize + itemSize > maximumSize) break;
                        result.Add(first.Value.Value); totalSize += itemSize;
                        index.Remove(first.Value.Key); items.RemoveFirst();
                    }
                    return result;
                }
                if (completed) return [];
            }
            await changed.WaitAsync(token);
        }
    }
}
