using BlueJays.Core.Models;

namespace BlueJays.Core.Audio;

/// <summary>Autoregressive Qwen should spend its time on final speech, not repeated drafts.</summary>
public static class SpeechTuning
{
    public static bool EnablePreviews(AppSettings settings) => settings.SpeechEngine != SpeechEngine.Qwen3Asr || settings.QwenLivePreviews;
    public static double MinimumPreviewSeconds(AppSettings settings) => settings.SpeechEngine switch
    {
        SpeechEngine.Qwen3Asr => 6,
        SpeechEngine.SenseVoice => 1.8,
        _ => 1.2
    };
}
