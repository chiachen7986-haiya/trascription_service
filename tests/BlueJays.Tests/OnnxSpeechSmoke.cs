using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using NAudio.Wave;

internal static class OnnxSpeechSmoke
{
    public static async Task<int> CancelAsync(string[] args)
    {
        var folder = Path.GetFullPath(args[1]);
        await using var recognizer = new OnnxSpeechRecognizer(new() { SpeechEngine = SpeechEngine.Qwen3Asr,
            QwenModelPath = folder, SpeechDevice = SpeechDevice.Cpu }, new());
        await recognizer.WarmAsync(default);
        using var reader = new WaveFileReader(args[2]); var samples = new float[reader.SampleCount];
        var count = reader.ToSampleProvider().Read(samples, 0, samples.Length); Array.Resize(ref samples, count);
        using var cancel = new CancellationTokenSource();
        var task = recognizer.RecognizeAsync(new("fixture", "fixture", new("cancel", 1, DateTimeOffset.Now, samples, true, false)), cancel.Token);
        await Task.Delay(10); var clock = Stopwatch.StartNew(); await cancel.CancelAsync();
        try { await task; throw new Exception("The in-flight decode was not canceled."); }
        catch (OperationCanceledException) { }
        if (clock.Elapsed.TotalSeconds > 2) throw new Exception("Native cancellation blocked for more than two seconds.");
        Console.WriteLine($"PASS in-flight Qwen cancellation: {clock.Elapsed.TotalMilliseconds:F0} ms"); return 0;
    }
    // Runs the production host against public fixture files. No devices, API,
    // user settings or desktop input are used.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 6) throw new ArgumentException("--onnx-smoke <sensevoice|qwen> <model-folder> <cpu|openvino-cpu|gpu> <ja.wav> <en.wav> [output.json]");
        var engine = args[1] == "qwen" ? SpeechEngine.Qwen3Asr : SpeechEngine.SenseVoice;
        var device = args[3] switch { "cpu" => SpeechDevice.Cpu, "openvino-cpu" => SpeechDevice.OpenVinoCpu, _ => SpeechDevice.OpenVinoGpu };
        var folder = Path.GetFullPath(args[2]);
        var settings = new AppSettings { SpeechEngine = engine, SenseVoiceModelPath = folder, QwenModelPath = folder,
            SpeechDevice = device, AllowSpeechCpuFallback = args.Contains("fallback") };
        var clock = Stopwatch.StartNew();
        var rows = new List<object>();
        var customHost = args.FirstOrDefault(a => a.StartsWith("host=", StringComparison.Ordinal))?[5..];
        await using (var recognizer = new OnnxSpeechRecognizer(settings, new() { SpokenLanguages = ["en", "ja"] }, customHost))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await recognizer.WarmAsync(deadline.Token); var startup = clock.Elapsed.TotalSeconds;
            if (args.Contains("fallback") && (recognizer.UsesGpu || recognizer.StartupWarning is null)) throw new Exception("Fallback was not reported or retained a GPU label.");
            foreach (var path in args.Skip(4).Take(2))
            {
                using var reader = new WaveFileReader(path);
                if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1) throw new ArgumentException("Use mono 16 kHz WAV.");
                var samples = new float[reader.SampleCount]; var count = reader.ToSampleProvider().Read(samples, 0, samples.Length);
                Array.Resize(ref samples, count);
                var capture = new CapturedWindow("fixture", "fixture", new(Guid.NewGuid().ToString("N"), 1, DateTimeOffset.Now, samples, true, false, SpeechEndSample: samples.Length));
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    clock.Restart(); var parts = await recognizer.RecognizeAsync(capture, deadline.Token);
                    var text = string.Concat(parts.Select(p => p.Text)).Trim();
                    if (text.Length == 0) throw new Exception("The model returned no speech for a spoken fixture.");
                    if (args.Contains("verify-language"))
                    {
                        var expectedLanguage = path == args[4] ? "ja" : "en";
                        if (parts.Any(p => BlueJays.Core.Translation.TranslationLanguage.Normalize(p.Language) != expectedLanguage))
                            throw new Exception("Native speech language metadata was missing or incorrect.");
                    }
                    rows.Add(new { file = Path.GetFileName(path), repeat, audio_seconds = samples.Length / 16000d,
                        decode_seconds = clock.Elapsed.TotalSeconds, text, parts = parts.Count, languages = parts.Select(p => p.Language).Distinct().ToArray() });
                    Console.WriteLine($"{recognizer.BackendName}: {clock.Elapsed.TotalSeconds:F3}s: {text}");
                }
            }
            var json = JsonSerializer.Serialize(new { engine, device, recognizer.BackendName, recognizer.UsesGpu, startup_seconds = startup, rows }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (args.Length > 6) await File.WriteAllTextAsync(args[6], json);
        }
        return 0;
    }
}
