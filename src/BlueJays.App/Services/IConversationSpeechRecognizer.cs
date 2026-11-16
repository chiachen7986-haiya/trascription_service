using BlueJays.Core.Models;

namespace BlueJays.App.Services;

public interface IConversationSpeechRecognizer : IDisposable
{
    int WorkerCount { get; }
    bool UsesGpu { get; }
    string BackendName { get; }
    string? StartupWarning { get; }
    Task WarmAsync(CancellationToken token);
    Task<IReadOnlyList<RecognizedPart>> RecognizeAsync(CapturedWindow capture, CancellationToken token, string prompt = "");
    Task ForgetAsync(string utteranceId);
}

public static class ConversationSpeechRecognizer
{
    public static IConversationSpeechRecognizer Create(AppSettings settings, ConversationOptions conversation) => settings.SpeechEngine switch
    {
        SpeechEngine.Whisper => new RoutedSpeechRecognizer(settings, conversation),
        SpeechEngine.SenseVoice or SpeechEngine.Qwen3Asr => new OnnxSpeechRecognizer(settings, conversation),
        _ => throw new ArgumentException("Unknown speech engine.")
    };
}
