namespace BlueJays.Core.Audio;

/// <summary>Bounded gain helps quiet voices without turning silence into loud noise.</summary>
public static class SpeechAudio
{
    public static float[] Prepare(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return [];
        double sum = 0;
        foreach (var value in input) sum += float.IsFinite(value) ? value : 0;
        var mean = sum / input.Length;
        double energy = 0, peak = 0;
        foreach (var value in input)
        {
            var sample = float.IsFinite(value) ? value - mean : 0;
            energy += sample * sample; peak = Math.Max(peak, Math.Abs(sample));
        }
        var rms = Math.Sqrt(energy / input.Length);
        var gain = rms > .001 && peak > .002 ? Math.Min(3, Math.Min(.12 / rms, .95 / peak)) : 1;
        var output = new float[Math.Max(16000, input.Length)];
        for (var i = 0; i < input.Length; i++) output[i] = float.IsFinite(input[i]) ? (float)((input[i] - mean) * gain) : 0;
        return output;
    }
}
