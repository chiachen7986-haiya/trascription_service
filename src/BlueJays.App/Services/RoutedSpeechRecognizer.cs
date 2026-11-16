using System.IO;
using BlueJays.Core.Audio;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;
using Whisper.net;

namespace BlueJays.App.Services;

public interface ISpeechDecoder : IDisposable
{
    bool UsesGpu { get; }
    Task WarmAsync(CancellationToken token) => Task.CompletedTask;
    Task<IReadOnlyList<RecognizedPart>> RecognizeAsync(float[] samples, CancellationToken token, string prompt = "", string? fixedLanguage = null);
}

public interface ISpeechLanguageDetector : IDisposable
{
    Task<LanguageDetection> DetectAsync(float[] samples, IReadOnlyList<string> languages, CancellationToken token);
}

/// <summary>
/// Detects with Tiny, then routes to independent fixed-language decoders. The two
/// native model contexts are never used simultaneously by the same lane. A separate
/// admission limit controls whether the shared GPU runs one or two inferences.
/// </summary>
public sealed class RoutedSpeechRecognizer : IConversationSpeechRecognizer
{
    private readonly ISpeechDecoder[] decoders;
    private readonly SemaphoreSlim[] lanes;
    private readonly SemaphoreSlim admission;
    private readonly ISpeechLanguageDetector? detector;
    private readonly LanguageRouteCache routes;
    private readonly string[] languages;
    private readonly Dictionary<string, int> assignments = new();
    private readonly object assignmentGate = new();
    public int WorkerCount => decoders.Length;
    public int GpuDecoderCount => decoders.Count(d => d.UsesGpu);
    public bool UsesGpu => GpuDecoderCount == WorkerCount;
    public int ConcurrentDecodes { get; }
    public string BackendName => GpuDecoderCount == 0 ? "Whisper · CPU" : UsesGpu ? "Whisper · Vulkan" : "Whisper · Vulkan / CPU";
    public string? StartupWarning => GpuDecoderCount != WorkerCount
        ? "Vulkan could not be enabled for every decoder. CPU fallback is active." : null;
    public event Action<string, string>? LanguageSelected;

    public RoutedSpeechRecognizer(AppSettings settings, ConversationOptions conversation)
        : this(conversation.SpokenLanguages,
            _ => new WhisperTranscriber(settings.ModelPath, "en", settings.UseBeamSearch, settings.NeuralSpeechDetection, useGpu: settings.UseGpuAcceleration),
            conversation.SpokenLanguages.Where(l => l != "auto").Distinct().Count() == 1 ? null : new TinyLanguageDetector(settings.ModelPath),
            settings.ParallelLanguageDecoding)
    { }

    // Injectable native boundaries allow concurrency, cancellation and routing tests without a GPU.
    public RoutedSpeechRecognizer(IReadOnlyList<string> languages, Func<int, ISpeechDecoder> createDecoder,
        ISpeechLanguageDetector? detector, bool parallelGpuDecoding)
    {
        this.languages = languages.Where(l => l != "auto").Distinct().ToArray();
        this.detector = detector; routes = new(this.languages);
        var created = new List<ISpeechDecoder>();
        try
        {
            for (var i = 0; i < (this.languages.Length == 1 ? 1 : 2); i++) created.Add(createDecoder(i));
            decoders = created.ToArray(); lanes = decoders.Select(_ => new SemaphoreSlim(1)).ToArray();
            ConcurrentDecodes = parallelGpuDecoding && UsesGpu ? WorkerCount : 1;
            admission = new(ConcurrentDecodes);
            for (var i = 0; i < this.languages.Length; i++) assignments[this.languages[i]] = i % WorkerCount;
        }
        catch { foreach (var item in created) item.Dispose(); detector?.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<RecognizedPart>> RecognizeAsync(CapturedWindow capture, CancellationToken token, string prompt = "")
    {
        var language = await routes.ResolveAsync(capture.Window.Id, capture.SourceId, capture.Window.SpeechEndSample,
            ct => detector?.DetectAsync(capture.Window.Samples, languages, ct) ?? Task.FromResult(new LanguageDetection(languages.FirstOrDefault() ?? "en", 0)), token);
        LanguageSelected?.Invoke(capture.Window.Id, language);
        int lane;
        lock (assignmentGate)
        {
            if (!assignments.TryGetValue(language, out lane)) assignments[language] = lane = assignments.Count % WorkerCount;
        }
        await lanes[lane].WaitAsync(token);
        try
        {
            await admission.WaitAsync(token);
            try
            {
                var parts = await decoders[lane].RecognizeAsync(capture.Window.Samples, token, prompt, language);
                var reliable = await routes.ReliableLanguageAsync(capture.Window.Id, token);
                return parts.Select(p => p with { Language = reliable }).ToArray();
            }
            finally { admission.Release(); }
        }
        finally { lanes[lane].Release(); }
    }

    public Task ForgetAsync(string utteranceId) => routes.ForgetAsync(utteranceId);
    public async Task WarmAsync(CancellationToken token)
    {
        foreach (var decoder in decoders) await decoder.WarmAsync(token);
    }
    public void Dispose()
    {
        foreach (var decoder in decoders) decoder.Dispose();
        detector?.Dispose(); foreach (var lane in lanes) lane.Dispose(); admission.Dispose();
    }
}

/// <summary>A small CPU-only language classifier; the main speech model never uses auto decoding.</summary>
public sealed class TinyLanguageDetector : ISpeechLanguageDetector
{
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;

    public TinyLanguageDetector(string transcriptionModel)
    {
        var tiny = Path.Combine(AppContext.BaseDirectory, "models", "ggml-tiny.bin");
        if (!File.Exists(tiny)) tiny = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(transcriptionModel))!, "ggml-tiny.bin");
        // Company-staged model-only installs still work; packaged releases always include Tiny.
        (factory, _) = SpeechRuntime.Load(File.Exists(tiny) ? tiny : transcriptionModel, false);
        try { processor = factory.CreateBuilder().WithLanguage("auto").WithNoContext().WithThreads(Math.Clamp(Environment.ProcessorCount / 3, 1, 4)).Build(); }
        catch { factory.Dispose(); throw; }
    }

    public Task<LanguageDetection> DetectAsync(float[] samples, IReadOnlyList<string> languages, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        // Keep enough acoustic context: a short Japanese sentence ending alone can
        // be misclassified as English. Bound long turns to the latest eight seconds.
        var prepared = SpeechAudio.Prepare(samples.AsSpan(Math.Max(0, samples.Length - 128000)));
        var (language, probability) = processor.DetectLanguageWithProbability(prepared.AsSpan(), languages.ToArray().AsSpan());
        token.ThrowIfCancellationRequested(); return new LanguageDetection(language ?? "", probability);
    }, token);

    public void Dispose() { processor.Dispose(); factory.Dispose(); }
}
