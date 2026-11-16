using BlueJays.Core.Models;

namespace BlueJays.Core.Conversation;

/// <summary>
/// Coalesces pending previews while allowing independent sources to decode concurrently.
/// Each source stays ordered, including overlapping successor chunks. This preserves
/// recognition context, stable-clause acknowledgements and boundary deduplication.
/// </summary>
public sealed class SpeechWorkQueue(int capacity = 64)
{
    private readonly object gate = new();
    private readonly LinkedList<CapturedWindow> pending = new();
    private readonly Dictionary<string, LinkedListNode<CapturedWindow>> index = new();
    private readonly Dictionary<string, CapturedWindow> running = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private bool completed;
    public bool HasFinalPending { get { lock (gate) return pending.Any(c => c.Window.IsFinal); } }

    public bool Put(CapturedWindow capture)
    {
        lock (gate)
        {
            if (completed) return false;
            var id = capture.Window.Id;
            if (index.TryGetValue(id, out var node))
            {
                // A delayed callback must never replace final audio with a draft.
                if (capture.Window.Revision >= node.Value.Window.Revision && (!node.Value.Window.IsFinal || capture.Window.IsFinal))
                    node.Value = capture;
            }
            else
            {
                if (pending.Count >= capacity)
                {
                    // Previews are replaceable. Make room for completed speech;
                    // never discard a final utterance to retain an optional draft.
                    var draft = pending.First;
                    while (draft is not null && draft.Value.Window.IsFinal) draft = draft.Next;
                    if (draft is null) return !capture.Window.IsFinal;
                    index.Remove(draft.Value.Window.Id); pending.Remove(draft);
                }
                index[id] = pending.AddLast(capture);
            }
            Signal(); return true;
        }
    }

    public async Task<CapturedWindow?> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                for (var node = pending.First; node is not null; node = node.Next)
                {
                    var capture = node.Value;
                    if (running.Values.Any(other => other.SourceId == capture.SourceId)) continue;
                    pending.Remove(node); index.Remove(capture.Window.Id);
                    running.Add(capture.Window.Id, capture); Signal(); return capture;
                }
                if (completed && pending.Count == 0) { Signal(); return null; }
            }
            await changed.WaitAsync(token);
        }
    }

    public void Release(string id) { lock (gate) { running.Remove(id); Signal(); } }
    public void Complete() { lock (gate) { completed = true; Signal(); } }
    private void Signal() { if (changed.CurrentCount == 0) changed.Release(); }
}
