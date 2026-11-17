namespace BlueJays.Core.Models;

public enum AudioSourceKind { Microphone, ComputerAudio }
public enum SpeechEngine { Whisper, SenseVoice, Qwen3Asr }
public enum SpeechDevice { OpenVinoGpu, OpenVinoCpu, Cpu }

public sealed record AudioSourceSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public AudioSourceKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record AppSettings
{
    public string UiLanguage { get; init; } = "en";
    public string ModelPath { get; init; } = "";
    // Keep independent paths so switching engines does not discard a staged model.
    public SpeechEngine SpeechEngine { get; init; } = SpeechEngine.Whisper;
    public string SenseVoiceModelPath { get; init; } = "";
    public string QwenModelPath { get; init; } = "";
    public SpeechDevice SpeechDevice { get; init; } = SpeechDevice.Cpu;
    public bool AllowSpeechCpuFallback { get; init; } = true;
    public int SpeechCpuThreads { get; init; } = 6;
    public bool QwenLivePreviews { get; init; }
    public string SpeechLanguage { get; init; } = "auto";
    public string TargetLanguage { get; init; } = "English";
    public bool TranslationEnabled { get; init; }
    public string ApiBaseUrl { get; init; } = "https://api.deepseek.com/v1";
    public string ApiModel { get; init; } = "deepseek-flash";
    public bool SendThinkingDisabled { get; init; } = true;
    public bool ContextualCorrectionEnabled { get; init; } = true;
    public bool UseBeamSearch { get; init; }
    public bool NeuralSpeechDetection { get; init; } = true;
    public bool MicrophoneNoiseSuppression { get; init; } = true;
    public bool UseGpuAcceleration { get; init; } = true;
    public bool ParallelLanguageDecoding { get; init; }
    public int SpeechProfileVersion { get; init; }
    public int SilenceMilliseconds { get; init; } = 1000;
    public int MaxPhraseSeconds { get; init; } = 8;
    public double SpeechThreshold { get; init; } = 0.012;
    public List<AudioSourceSettings> Sources { get; init; } = [];
}

public sealed record Transcript(string Id, string SourceName, DateTimeOffset CapturedAt, string Text,
    string SourceId = "", int Revision = 1, bool IsFinal = true, int StableLength = 0, string RawText = "", string Language = "");

public sealed record ConversationOptions
{
    public string Name { get; init; } = "Conversation";
    public List<string> SpokenLanguages { get; init; } = ["en", "ja"];
    public List<string> TargetLanguages { get; init; } = ["English", "Japanese"];
    public string Topic { get; init; } = "";
    public string Glossary { get; init; } = "";
    public Guid? TerminologyDictionaryId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RecognitionGlossary { get; init; }
    public string SpeechLanguage => SpokenLanguages.Distinct().Count() == 1 ? SpokenLanguages[0] : "auto";
}

public sealed record SpeechWindow(string Id, int Revision, DateTimeOffset StartedAt, float[] Samples, bool IsFinal, bool HasOverlap,
    long StartSample = 0, long SpeechEndSample = 0);
public sealed record CapturedWindow(string SourceId, string SourceName, SpeechWindow Window);
public sealed record TranslationItem(string Id, string Source, string Text, int Revision, bool IsFinal, string Language = "");
public sealed record TranslationResult(string Id, string Language, string Text);
public sealed record TranscriptCorrection(string Id, string Text);
public sealed record RecognizedPart(string Text, double StartSeconds, double EndSeconds, string Language = "");
