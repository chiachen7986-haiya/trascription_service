using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

// Isolated evaluation tool. It never loads saved settings, captures/plays audio,
// contacts a translation provider, or changes the application's selected model.
if (args.FirstOrDefault() == "prepare") return Fixtures.Prepare(Directory.GetCurrentDirectory());
if (args.FirstOrDefault() == "fleurs") return await Fixtures.PrepareFleursAsync(Directory.GetCurrentDirectory());
if (args.FirstOrDefault() == "report") return ReportBuilder.Build(Directory.GetCurrentDirectory());
if (args.FirstOrDefault() == "self-test")
{
    if (NormalizeCharacters("Ａ 会。 ５０") != "a会50" || Distance(["a", "b", "c"], ["a", "c"]) != 1 ||
        Distance(["弁", "当", "制"], ["弁", "当", "性"]) != 1 || !Words("Don't, stop!").SequenceEqual(["dont", "stop"]))
        throw new Exception("Scoring self-check failed.");
    Console.WriteLine("PASS CER normalization, homophone substitution, edit distance and WER tokenization."); return 0;
}
if (args.Length < 4)
    throw new ArgumentException("<sensevoice|qwen|whisper-base|whisper-small|whisper-routed> <model-path> <manifest.json> <output.json> [provider=cpu] [threads=6] [runs=3] [fixed]");
var engineName = args[0]; var modelPath = Path.GetFullPath(args[1]);
var manifestPath = Path.GetFullPath(args[2]); var outputPath = Path.GetFullPath(args[3]);
var provider = Option("provider", "cpu"); var threads = int.Parse(Option("threads", "6"), CultureInfo.InvariantCulture);
var runs = Math.Clamp(int.Parse(Option("runs", "3"), CultureInfo.InvariantCulture), 1, 10);
if (threads < 1 || threads > Environment.ProcessorCount) throw new ArgumentOutOfRangeException(nameof(threads));
var fixedLanguage = args.Contains("fixed");
if (fixedLanguage && engineName == "qwen") throw new ArgumentException("This sherpa Qwen export exposes automatic language selection only.");
var clips = JsonSerializer.Deserialize<Clip[]>(File.ReadAllText(manifestPath), JsonOptions())!;
if (fixedLanguage && clips.Select(c => c.Language).Distinct().Count() > 1) throw new ArgumentException("Use a single-language manifest with fixed SenseVoice.");
if (engineName.StartsWith("whisper", StringComparison.Ordinal) && (threads != 6 || provider is not ("cpu" or "vulkan")))
    throw new ArgumentException("The app's Whisper decoder uses six CPU threads on this laptop. Choose provider=cpu or provider=vulkan.");
var process = Process.GetCurrentProcess(); var clock = Stopwatch.StartNew();
using var engine = CreateEngine(); var loadSeconds = clock.Elapsed.TotalSeconds;
var rows = new List<object>();
foreach (var clip in clips)
{
    var path = Path.GetFullPath(clip.Path, Path.GetDirectoryName(manifestPath)!);
    var audio = ReadWave(path); var duration = audio.Length / 16000d;
    clock.Restart(); var text = await engine.DecodeAsync(audio, clip.Language); var first = clock.Elapsed.TotalSeconds;
    var times = new List<double>(); var cpuBefore = process.TotalProcessorTime.TotalSeconds;
    for (var repeat = 0; repeat < runs; repeat++)
    {
        clock.Restart(); var repeated = await engine.DecodeAsync(audio, clip.Language); times.Add(clock.Elapsed.TotalSeconds);
        if (repeated != text) Console.WriteLine($"NOTE nonidentical repeated result: {clip.Id}");
    }
    var cpu = process.TotalProcessorTime.TotalSeconds - cpuBefore;
    var median = times.Order().ElementAt(times.Count / 2);
    var reference = NormalizeCharacters(clip.Reference); var hypothesis = NormalizeCharacters(text);
    var characterErrors = reference.Length == 0 ? (int?)null : Distance(reference.Select(c => c.ToString()).ToArray(), hypothesis.Select(c => c.ToString()).ToArray());
    var referenceWords = Words(clip.Reference); var wordErrors = clip.Language == "en" && referenceWords.Length > 0 ? Distance(referenceWords, Words(text)) : (int?)null;
    process.Refresh();
    rows.Add(new { clip.Id, clip.Language, clip.Source, clip.Reference, audio_seconds = duration, first_decode_seconds = first,
        warm_seconds = times, median_seconds = median, real_time_factor = median / duration, cpu_seconds_per_decode = cpu / runs,
        text, reference_characters = reference.Length, character_errors = characterErrors,
        character_error_rate = characterErrors / (double?)reference.Length, reference_words = referenceWords.Length,
        word_errors = wordErrors, word_error_rate = wordErrors / (double?)referenceWords.Length,
        working_set_bytes = process.WorkingSet64, peak_working_set_bytes = process.PeakWorkingSet64 });
    Console.WriteLine($"{clip.Id}: {duration:F2}s audio; {median:F3}s median; RTF {median / duration:F3}; CER {characterErrors}/{reference.Length}");
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(new { engine = engineName, engine.Backend, threads, provider_requested = provider,
    language_mode = fixedLanguage || engineName is "whisper-base" or "whisper-small" ? "fixed" : "automatic", runs, load_seconds = loadSeconds,
    model_path = modelPath, manifest = manifestPath, utc_time = DateTimeOffset.UtcNow, clips = rows }, JsonOptions()), new UTF8Encoding(false));
Console.WriteLine("Saved " + outputPath);
return 0;

string Option(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? fallback;
IBenchEngine CreateEngine() => engineName switch
{
    "sensevoice" or "qwen" => new SherpaEngine(engineName, modelPath, threads, provider, fixedLanguage ? clips[0].Language : "auto"),
    "whisper-base" or "whisper-small" => new WhisperEngine(modelPath, provider != "cpu"),
    "whisper-routed" => new RoutedEngine(modelPath, provider != "cpu", clips.Select(c => c.Language).Concat(["en", "ja"]).Distinct().ToList()),
    _ => throw new ArgumentException("Unknown engine.")
};
static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
static float[] ReadWave(string path)
{
    using var reader = new WaveFileReader(path); ISampleProvider source = reader.ToSampleProvider();
    if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source);
    if (source.WaveFormat.Channels != 1) throw new ArgumentException("Use mono/stereo WAV.");
    if (source.WaveFormat.SampleRate != 16000) source = new WdlResamplingSampleProvider(source, 16000);
    var samples = new List<float>(); var buffer = new float[16000]; int count;
    while ((count = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, count).ToArray());
    return samples.ToArray();
}
static string NormalizeCharacters(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit));
static string[] Words(string text) => Regex.Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace("'", "").Replace("’", ""), @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();
static int Distance(string[] reference, string[] hypothesis)
{
    var row = Enumerable.Range(0, hypothesis.Length + 1).ToArray();
    for (var i = 1; i <= reference.Length; i++)
    {
        var next = new int[hypothesis.Length + 1]; next[0] = i;
        for (var j = 1; j <= hypothesis.Length; j++)
            next[j] = Math.Min(Math.Min(row[j] + 1, next[j - 1] + 1), row[j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1));
        row = next;
    }
    return row[^1];
}

internal sealed record Clip(string Id, string Path, string Language, string Reference, string Source);
internal interface IBenchEngine : IDisposable
{
    string Backend { get; }
    Task<string> DecodeAsync(float[] audio, string language);
}
internal sealed class SherpaEngine : IBenchEngine
{
    private readonly OfflineRecognizer recognizer;
    public string Backend { get; }
    public SherpaEngine(string engine, string folder, int threads, string provider, string language)
    {
        var config = new OfflineRecognizerConfig(); config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = provider;
        if (engine == "sensevoice")
        {
            config.ModelConfig.SenseVoice.Model = Path.Combine(folder, "model.int8.onnx");
            config.ModelConfig.SenseVoice.Language = language;
            config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            config.ModelConfig.Tokens = Path.Combine(folder, "tokens.txt");
        }
        else
        {
            config.ModelConfig.Qwen3Asr.ConvFrontend = Path.Combine(folder, "conv_frontend.onnx");
            config.ModelConfig.Qwen3Asr.Encoder = Path.Combine(folder, "encoder.int8.onnx");
            config.ModelConfig.Qwen3Asr.Decoder = Path.Combine(folder, "decoder.int8.onnx");
            config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(folder, "tokenizer");
            config.ModelConfig.Qwen3Asr.MaxNewTokens = 256;
        }
        recognizer = new(config);
        Backend = "sherpa-onnx 2.0.1 / provider requested: " + provider;
    }
    public Task<string> DecodeAsync(float[] audio, string language)
    {
        using var stream = recognizer.CreateStream(); stream.AcceptWaveform(16000, audio);
        recognizer.Decode(stream); return Task.FromResult(stream.Result.Text);
    }
    public void Dispose() => recognizer.Dispose();
}
internal sealed class WhisperEngine(string path, bool gpu) : IBenchEngine
{
    private readonly WhisperTranscriber transcriber = new(path, "en", useGpu: gpu);
    public string Backend => transcriber.UsesGpu ? "Whisper.net 1.9.1 Vulkan" : "Whisper.net 1.9.1 CPU";
    public async Task<string> DecodeAsync(float[] audio, string language) => string.Concat((await transcriber.RecognizeAsync(audio, default, fixedLanguage: language)).Select(p => p.Text)).Trim();
    public void Dispose() => transcriber.Dispose();
}
internal sealed class RoutedEngine : IBenchEngine
{
    private readonly RoutedSpeechRecognizer recognizer;
    public string Backend => "Whisper.net 1.9.1 routed / " + (recognizer.UsesGpu ? "Vulkan" : "CPU");
    public RoutedEngine(string path, bool gpu, List<string> languages) => recognizer = new(new AppSettings { ModelPath = path, UseGpuAcceleration = gpu }, new() { SpokenLanguages = languages });
    public async Task<string> DecodeAsync(float[] audio, string language)
    {
        var id = Guid.NewGuid().ToString("N");
        try { return string.Concat((await recognizer.RecognizeAsync(new(id, id, new(id, 1, DateTimeOffset.Now, audio, true, false, SpeechEndSample: audio.Length)), default)).Select(p => p.Text)).Trim(); }
        finally { await recognizer.ForgetAsync(id); }
    }
    public void Dispose() => recognizer.Dispose();
}
