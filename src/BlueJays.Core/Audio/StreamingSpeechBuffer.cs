using BlueJays.Core.Models;

namespace BlueJays.Core.Audio;

/// <summary>
/// Emits only the uncommitted audio tail. The recognizer acknowledges timestamped,
/// stable clauses so subsequent passes do not decode the whole utterance again.
/// Slow decoders increase the preview interval; final audio is always retained.
/// Access is serialized by the capture source.
/// </summary>
public sealed class StreamingSpeechBuffer(double threshold = .012, int silenceMilliseconds = 1000, int maxSeconds = 8,
    bool enablePreviews = true, double minimumPreviewSeconds = 1.2)
{
    private const int Rate = 16000, FrameSize = 320;
    private readonly float[] frame = new float[FrameSize];
    private readonly Queue<float[]> preRoll = new();
    private readonly List<float> samples = [];
    private readonly AdaptiveSpeechGate detector = new(threshold);
    private float[] overlap = [];
    private string id = "";
    private DateTimeOffset started;
    private int fill, quiet, voiced, sinceUpdate, revision;
    private bool hasOverlap;
    private long startSample, speechEndSample;
    private readonly double previewFloor = Math.Clamp(minimumPreviewSeconds, 1.2, 20);
    private int previewSamples = (int)(Rate * Math.Clamp(minimumPreviewSeconds, 1.2, 20));
    public event Action<SpeechWindow>? Updated;

    public void Push(ReadOnlySpan<float> input)
    {
        while (!input.IsEmpty)
        {
            var count = Math.Min(FrameSize - fill, input.Length);
            input[..count].CopyTo(frame.AsSpan(fill));
            fill += count; input = input[count..];
            if (fill != FrameSize) continue;
            fill = 0;
            ProcessFrame();
        }
    }

    private void ProcessFrame()
    {
        var speech = detector.IsSpeech(frame);
        if (id.Length == 0)
        {
            preRoll.Enqueue((float[])frame.Clone());
            while (preRoll.Count > 8) preRoll.Dequeue();
            if (!speech) { if (++quiet > 25) overlap = []; return; }
            id = Guid.NewGuid().ToString("N"); revision = 0;
            startSample = speechEndSample = 0;
            started = DateTimeOffset.Now;
            hasOverlap = overlap.Length > 0;
            samples.AddRange(overlap); overlap = [];
            foreach (var saved in preRoll) samples.AddRange(saved);
            preRoll.Clear(); quiet = 0;
        }
        else samples.AddRange(frame);

        voiced += speech ? FrameSize : 0;
        if (speech) speechEndSample = startSample + samples.Count;
        quiet = speech ? 0 : quiet + 20;
        sinceUpdate += FrameSize;
        var maximum = samples.Count >= Math.Clamp(maxSeconds, 8, 20) * Rate;
        if (quiet >= Math.Clamp(silenceMilliseconds, 700, 2000) || maximum)
        {
            Emit(true);
            overlap = maximum ? samples.TakeLast(Rate * 3 / 2).ToArray() : [];
            Reset();
        }
        else if (enablePreviews && sinceUpdate >= previewSamples && voiced >= Rate / 4) Emit(false);
    }

    private void Emit(bool final)
    {
        if (voiced < Rate / 5) return;
        // Keep 180 ms of trailing silence rather than decoding the entire pause.
        var end = (int)Math.Min(samples.Count, speechEndSample - startSample + Rate * .18);
        Updated?.Invoke(new(id, ++revision, started, samples.Take(Math.Max(0, end)).ToArray(), final, hasOverlap, startSample, speechEndSample));
        sinceUpdate = 0;
    }

    public void Acknowledge(string windowId, long throughSample, double decodeSeconds)
    {
        // Reserve headroom for final passes, language detection and other sources.
        // A slow decoder must be allowed to stop producing redundant previews.
        // A cached final result costs no inference. It must not reset the adaptive
        // interval and flood a slow model with previews on the next utterance.
        if (decodeSeconds > 0)
            previewSamples = (int)(Rate * Math.Clamp(decodeSeconds * 2.2, previewFloor, Math.Max(previewFloor, Math.Clamp(maxSeconds, 8, 20))));
        if (id != windowId || throughSample <= startSample) return;
        // A small acoustic overlap protects word boundaries. Offsets remain absolute
        // within this utterance, including snapshots already waiting in the queue.
        var remove = (int)Math.Clamp(throughSample - Rate * .3 - startSample, 0, Math.Max(0, samples.Count - Rate / 2));
        samples.RemoveRange(0, remove); startSample += remove;
    }

    public void Flush()
    {
        // Finalize even a short ending when the user stops a conversation.
        if (id.Length > 0) Emit(true);
        overlap = []; preRoll.Clear(); Reset();
    }

    private void Reset() { id = ""; samples.Clear(); quiet = voiced = sinceUpdate = 0; }
}
