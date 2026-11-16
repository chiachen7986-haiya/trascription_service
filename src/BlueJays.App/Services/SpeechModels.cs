using System.IO;
using BlueJays.Core.Models;

namespace BlueJays.App.Services;

public sealed record SpeechModelPackage(SpeechEngine Engine, string Folder, string Sha256, long DownloadBytes, string[] RequiredFiles)
{
    public string DownloadUrl => "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/" + Folder + ".tar.bz2";
}

/// <summary>Pinned official exports; each engine retains its own path and model weights.</summary>
public static class SpeechModels
{
    public static readonly SpeechModelPackage SenseVoice = new(SpeechEngine.SenseVoice,
        "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17",
        "7d1efa2138a65b0b488df37f8b89e3d91a60676e416f515b952358d83dfd347e", 163002883,
        ["model.int8.onnx", "tokens.txt"]);
    public static readonly SpeechModelPackage Qwen = new(SpeechEngine.Qwen3Asr,
        "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25",
        "393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96", 878702423,
        ["conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx", "tokenizer/vocab.json", "tokenizer/merges.txt", "tokenizer/tokenizer_config.json"]);
    public static SpeechModelPackage Package(SpeechEngine engine) => engine switch
    {
        SpeechEngine.SenseVoice => SenseVoice, SpeechEngine.Qwen3Asr => Qwen,
        _ => throw new ArgumentException("Whisper uses a GGML model file.")
    };
    public static string PathFor(AppSettings settings) => settings.SpeechEngine switch
    {
        SpeechEngine.SenseVoice => Resolve(settings.SenseVoiceModelPath, SenseVoice),
        SpeechEngine.Qwen3Asr => Resolve(settings.QwenModelPath, Qwen),
        _ => settings.ModelPath
    };
    private static string Resolve(string saved, SpeechModelPackage package)
    {
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        var bundled = Path.Combine(AppContext.BaseDirectory, "models", package.Folder);
        return Directory.Exists(bundled) ? bundled : Path.Combine(SettingsStore.DataDirectory, "models", package.Folder);
    }
    public static bool IsReady(AppSettings settings) => settings.SpeechEngine == SpeechEngine.Whisper
        ? File.Exists(settings.ModelPath) : MissingFiles(settings.SpeechEngine, PathFor(settings)).Length == 0;
    public static string[] MissingFiles(SpeechEngine engine, string folder) => Package(engine).RequiredFiles
        .Where(file => !File.Exists(Path.Combine(folder, file)) || new FileInfo(Path.Combine(folder, file)).Length == 0).ToArray();
    public static void Validate(AppSettings settings, ConversationOptions conversation)
    {
        if (!IsReady(settings)) throw new FileNotFoundException("Select or download the speech model in Settings first.");
        if (settings.SpeechEngine == SpeechEngine.SenseVoice && conversation.SpokenLanguages.Any(l => l is not ("auto" or "en" or "ja" or "zh" or "ko" or "yue")))
            throw new ArgumentException("SenseVoice supports English, Japanese, Chinese, Korean and Cantonese. Choose Whisper or Qwen for other languages.");
    }
}
