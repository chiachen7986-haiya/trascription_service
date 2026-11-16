namespace BlueJays.Core.Audio;

/// <summary>Tracks quiet background energy; hysteresis and attack reject isolated clicks.</summary>
public sealed class AdaptiveSpeechGate(double sensitivity)
{
    private double noise = Math.Clamp(sensitivity / 4, .0003, .01);
    private int attack, hold;
    public bool IsSpeech(ReadOnlySpan<float> frame)
    {
        double sum = 0, square = 0;
        foreach (var value in frame) { if (!float.IsFinite(value)) continue; sum += value; square += value * value; }
        var rms = Math.Sqrt(Math.Max(0, square / frame.Length - Math.Pow(sum / frame.Length, 2)));
        var onset = Math.Max(Math.Clamp(sensitivity, .002, .08) * .65, noise * 2.8);
        var active = rms >= (hold > 0 ? onset * .65 : onset);
        if (active) { attack++; if (attack >= 2 || hold > 0) hold = 6; }
        else { attack = 0; if (hold > 0) hold--; }
        if (!active && hold == 0) noise += .025 * (Math.Min(rms, onset) - noise);
        return active && (attack >= 2 || hold > 0);
    }
}
