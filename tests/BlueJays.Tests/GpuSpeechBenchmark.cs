using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Models;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

internal static class GpuSpeechBenchmark
{
    public static Task<int> ProbeLanguageAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("--language-probe <model.bin> <wav>");
        var (factory, _) = SpeechRuntime.Load(args[1], false);
        using (factory)
        using (var processor = factory.CreateBuilder().WithThreads(4).Build())
        {
            var samples = Read(args[2]);
            foreach (var length in new[] { 1.2, 3, 6, samples.Length / 16000d })
            {
                var count = Math.Min(samples.Length, (int)(length * 16000)); var clock = Stopwatch.StartNew();
                var (language, probability) = processor.DetectLanguageWithProbability(samples.AsSpan(0, count), new[] { "ja", "en" }.AsSpan());
                Console.WriteLine(JsonSerializer.Serialize(new { model = Path.GetFileName(args[1]), audio_seconds = count / 16000d, language, probability, seconds = clock.Elapsed.TotalSeconds }));
            }
        }
        return Task.FromResult(0);
    }

    public static async Task<int> RunRoutedAsync(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("--routed-benchmark <model.bin> <ja.wav> <en.wav> [parallel] [cpu]");
        var settings = new AppSettings { ModelPath = args[1], UseGpuAcceleration = !args.Contains("cpu"), ParallelLanguageDecoding = args.Contains("parallel") };
        using var recognizer = new RoutedSpeechRecognizer(settings, new() { SpokenLanguages = ["ja", "en"] });
        var ja = Read(args[2]); var en = Read(args[3]);
        var selected = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        recognizer.LanguageSelected += (id, language) => selected[id] = language;
        CapturedWindow Capture(string id, float[] samples) => new(id, id,
            new(id, 1, DateTimeOffset.Now, samples, true, false, SpeechEndSample: samples.Length));
        var first = Capture("japanese", ja); var second = Capture("english", en);
        var clock = Stopwatch.StartNew();
        var jaParts = await recognizer.RecognizeAsync(first, default);
        var enParts = await recognizer.RecognizeAsync(second, default);
        var firstTime = clock.Elapsed.TotalSeconds;
        var rounds = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            // New utterances include Tiny language detection, not only cached previews.
            await recognizer.ForgetAsync(first.Window.Id); await recognizer.ForgetAsync(second.Window.Id);
            clock.Restart(); await Task.WhenAll(recognizer.RecognizeAsync(first, default), recognizer.RecognizeAsync(second, default));
            rounds.Add(clock.Elapsed.TotalSeconds);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeOptions.LoadedLibrary?.ToString(),
            gpu_used = recognizer.UsesGpu,
            model = Path.GetFileName(args[1]),
            workers = recognizer.WorkerCount,
            concurrent_decodes = recognizer.ConcurrentDecodes,
            japanese_audio_seconds = ja.Length / 16000d,
            english_audio_seconds = en.Length / 16000d,
            first_pair_seconds = firstTime,
            routed_pair_seconds = rounds,
            detected_languages = selected,
            japanese_text = string.Concat(jaParts.Select(p => p.Text)),
            english_text = string.Concat(enParts.Select(p => p.Text))
        },
            new JsonSerializerOptions { WriteIndented = true }));
        if (selected["japanese"] != "ja" || selected["english"] != "en" ||
            !string.Concat(jaParts.Select(p => p.Text)).Contains("中学") || !string.Concat(enParts.Select(p => p.Text)).Contains("Americans"))
            throw new Exception("Real language routing or speech recognition failed.");
        return 0;
    }

    // Run in its own process: a driver failure cannot affect an open meeting.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("--gpu-benchmark <model.bin> <ja.wav> <en.wav> [cpu]");
        using var logger = args.Contains("verbose") ? LogProvider.AddConsoleLogging(WhisperLogLevel.Info) : null;
        using var japanese = new WhisperTranscriber(args[1], "ja", useGpu: !args.Contains("cpu"));
        using var english = new WhisperTranscriber(args[1], "en", useGpu: !args.Contains("cpu"));
        var ja = Read(args[2]); var en = Read(args[3]);
        var clock = Stopwatch.StartNew();
        await japanese.RecognizeAsync(ja, default); await english.RecognizeAsync(en, default);
        var warmup = clock.Elapsed.TotalSeconds; clock.Restart();
        var first = await japanese.RecognizeAsync(ja, default); var jaTime = clock.Elapsed.TotalSeconds; clock.Restart();
        var second = await english.RecognizeAsync(en, default); var enTime = clock.Elapsed.TotalSeconds; clock.Restart();
        var sequential = new List<double>(); var parallel = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            clock.Restart(); await japanese.RecognizeAsync(ja, default); await english.RecognizeAsync(en, default);
            sequential.Add(clock.Elapsed.TotalSeconds);
            clock.Restart(); await Task.WhenAll(japanese.RecognizeAsync(ja, default), english.RecognizeAsync(en, default));
            parallel.Add(clock.Elapsed.TotalSeconds);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeOptions.LoadedLibrary?.ToString(),
            gpu_used = japanese.UsesGpu && english.UsesGpu,
            runtime_info = WhisperFactory.GetRuntimeInfo(),
            model = Path.GetFileName(args[1]),
            japanese_seconds = ja.Length / 16000d,
            english_seconds = en.Length / 16000d,
            warmup_seconds = warmup,
            japanese_decode_seconds = jaTime,
            english_decode_seconds = enTime,
            sequential_seconds = sequential,
            concurrent_seconds = parallel,
            japanese_text = string.Concat(first.Select(p => p.Text)),
            english_text = string.Concat(second.Select(p => p.Text))
        },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static float[] Read(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1) throw new ArgumentException("Use 16 kHz mono WAV.");
        var samples = new float[reader.SampleCount]; var count = reader.ToSampleProvider().Read(samples, 0, samples.Length);
        Array.Resize(ref samples, count); return samples;
    }
}
