using System.IO;
using System.Text;
using BlueJays.Core.Audio;
using BlueJays.Core.Models;
using Whisper.net;

namespace BlueJays.App.Services;

/// <summary>A warm model and reusable decoder. Calls are serialized by the session's speech worker.</summary>
public sealed class WhisperTranscriber : ISpeechDecoder
{
    private readonly WhisperFactory factory;
    private readonly string language;
    private readonly bool beamSearch;
    private readonly string[] allowedLanguages;
    private readonly WhisperVadFactory? vadFactory;
    private readonly WhisperVadProcessor? vad;
    private WhisperProcessor? processor;
    private string activePrompt = "";
    private string activeLanguage = "";
    public bool UsesGpu { get; }

    public WhisperTranscriber(string modelPath, string language, bool beamSearch = false, bool neuralDetection = false, IReadOnlyList<string>? allowedLanguages = null, bool useGpu = false)
    {
        (factory, UsesGpu) = SpeechRuntime.Load(modelPath, useGpu); this.language = language; this.beamSearch = beamSearch;
        this.allowedLanguages = allowedLanguages?.Where(l => l != "auto").Distinct().ToArray() ?? [];
        try
        {
            var vadPath = Path.Combine(AppContext.BaseDirectory, "models", "ggml-silero-v5.1.2.bin");
            if (!File.Exists(vadPath)) vadPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "ggml-silero-v5.1.2.bin");
            if (neuralDetection && File.Exists(vadPath))
            {
                vadFactory = WhisperVadFactory.FromPath(vadPath);
                vad = vadFactory.CreateBuilder().WithThreads(1).WithUseGpu(false).WithThreshold(.4f)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100)).WithMinSilenceDuration(TimeSpan.FromMilliseconds(160)).Build();
            }
        }
        catch { factory.Dispose(); vadFactory?.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<RecognizedPart>> RecognizeAsync(float[] samples, CancellationToken token, string prompt = "", string? fixedLanguage = null)
    {
        var decodingLanguage = fixedLanguage ?? language;
        var prepared = SpeechAudio.Prepare(samples);
        // Neural VAD checks the complete bounded tail before expensive decoding.
        if (vad is not null && (await vad.DetectSpeechAsync(prepared, token)).Count == 0) return [];
        EnsureProcessor(prompt, decodingLanguage);
        var parts = new List<RecognizedPart>();
        var detected = "";
        await foreach (var segment in processor!.ProcessAsync(prepared, token))
        {
            detected = segment.Language;
            if (!string.IsNullOrWhiteSpace(segment.Text))
                parts.Add(new(segment.Text, segment.Start.TotalSeconds, Math.Min(segment.End.TotalSeconds, samples.Length / 16000d)));
        }
        // Short mixed-language windows occasionally detect an unrelated language.
        // Only pay for a restricted retry in that case, not on every live pass.
        if (decodingLanguage == "auto" && allowedLanguages.Length > 0 && detected.Length > 0 && !allowedLanguages.Contains(detected))
        {
            var (selected, _) = processor.DetectLanguageWithProbability(prepared.AsSpan(), allowedLanguages.AsSpan());
            if (selected is not null)
            {
                processor.ChangeLanguage(selected);
                try
                {
                    parts.Clear();
                    await foreach (var segment in processor.ProcessAsync(prepared, token))
                        if (!string.IsNullOrWhiteSpace(segment.Text)) parts.Add(new(segment.Text, segment.Start.TotalSeconds, Math.Min(segment.End.TotalSeconds, samples.Length / 16000d)));
                }
                finally { processor.ChangeLanguage(decodingLanguage); }
            }
        }
        return parts;
    }

    public async Task WarmAsync(CancellationToken token)
    {
        // Compile/initialize GPU kernels before audio capture starts. Ignore all
        // output from synthetic silence; it never enters conversation history.
        EnsureProcessor("", language == "auto" ? "en" : language);
        await foreach (var _ in processor!.ProcessAsync(new float[16000], token)) { }
    }

    private void EnsureProcessor(string prompt, string decodingLanguage)
    {
        if (processor is not null && prompt == activePrompt && activeLanguage == decodingLanguage) return;
        processor?.Dispose();
        var builder = factory.CreateBuilder().WithLanguage(decodingLanguage).WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 6)).WithTemperature(0).WithTemperatureInc(0)
            .WithNoSpeechThreshold(.6f);
        if (beamSearch) builder.WithBeamSearchSamplingStrategy(strategy => strategy.WithBeamSize(3));
        if (!string.IsNullOrWhiteSpace(prompt)) builder.WithPrompt(prompt);
        processor = builder.Build(); activePrompt = prompt; activeLanguage = decodingLanguage;
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken token, string prompt = "") =>
        string.Concat((await RecognizeAsync(samples, token, prompt)).Select(p => p.Text)).Trim();

    public static string BuildPrompt(ConversationOptions conversation, string recentRawText)
    {
        // Whisper's prompt is a spelling hint. Do not feed API guesses back into STT.
        // Whisper keeps a short suffix of its prompt. Budget non-ASCII text more
        // conservatively and put explicit terminology last, so history cannot crowd it out.
        var glossary = Clip((conversation.RecognitionGlossary ?? conversation.Glossary).Trim(), 90);
        var topic = Clip(conversation.Topic.Trim(), 20);
        var recent = Clip(recentRawText, 50, true);
        return string.Join("\n", new[] { recent, topic, glossary }.Where(s => s.Length > 0));

        static string Clip(string value, double budget, bool tail = false)
        {
            var runes = value.EnumerateRunes().ToList(); if (tail) runes.Reverse();
            var selected = new List<Rune>();
            foreach (var rune in runes)
            {
                budget -= rune.IsAscii ? .3 : 2;
                if (budget < 0) break;
                selected.Add(rune);
            }
            if (tail) selected.Reverse();
            return string.Concat(selected.Select(r => r.ToString()));
        }
    }

    public void Dispose() { processor?.Dispose(); vad?.Dispose(); vadFactory?.Dispose(); factory.Dispose(); }
}
