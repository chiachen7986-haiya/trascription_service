using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Audio;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;
using NAudio.Wave;

internal static class SpeechBenchmark
{
    // Offline diagnostic: never plays audio, captures a microphone or calls an API.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("--benchmark <model.bin> <16k-mono.wav> <language> [beam]");
        var model = Path.GetFullPath(args[1]); using var reader = new WaveFileReader(Path.GetFullPath(args[2]));
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1) throw new ArgumentException("Use 16 kHz mono WAV.");
        var samples = new float[reader.SampleCount]; var count = reader.ToSampleProvider().Read(samples, 0, samples.Length); Array.Resize(ref samples, count);
        var clock = Stopwatch.StartNew();
        using var transcriber = new WhisperTranscriber(model, args[3], args.Contains("beam"), true);
        var load = clock.Elapsed.TotalSeconds; clock.Restart();
        var parts = await transcriber.RecognizeAsync(samples, default);
        var first = clock.Elapsed.TotalSeconds; clock.Restart();
        var repeated = await transcriber.TranscribeAsync(samples, default);
        var warm = clock.Elapsed.TotalSeconds;
        var assemblers = new Dictionary<string, LiveClauseAssembler>(); var captions = new Dictionary<string, Transcript>();
        var previousUtterance = "";
        var buffer = new StreamingSpeechBuffer(); var windows = new List<SpeechWindow>();
        buffer.Updated += windows.Add; var totalSamples = 0; clock.Restart();
        for (var cursor = 0; cursor < samples.Length; cursor += 320)
        {
            buffer.Push(samples.AsSpan(cursor, Math.Min(320, samples.Length - cursor)));
            if (cursor + 320 >= samples.Length) buffer.Flush();
            while (windows.Count > 0)
            {
                var window = windows[0]; windows.RemoveAt(0); totalSamples += window.Samples.Length;
                var pass = Stopwatch.StartNew();
                var recognized = await transcriber.RecognizeAsync(window.Samples, default);
                if (!assemblers.TryGetValue(window.Id, out var assembler)) assemblers[window.Id] = assembler = new();
                var update = assembler.Update(new("sample", "Sample", window), recognized, previousUtterance);
                foreach (var caption in update.Captions) captions[caption.Id] = caption;
                buffer.Acknowledge(window.Id, update.CommittedSample, pass.Elapsed.TotalSeconds);
                if (window.IsFinal)
                {
                    previousUtterance = string.Join(" ", update.Captions.Where(c => c.IsFinal).Select(c => c.Text));
                    assemblers.Remove(window.Id);
                }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            model = Path.GetFileName(model), language = args[3], beam = args.Contains("beam"), audio_seconds = samples.Length / 16000d,
            load_seconds = load, first_decode_seconds = first, warm_decode_seconds = warm, real_time_factor = warm / (samples.Length / 16000d),
            text = string.Concat(parts.Select(p => p.Text)).Trim(), repeated,
            simulated_stream_seconds = clock.Elapsed.TotalSeconds, decoded_audio_seconds = totalSamples / 16000d,
            clauses = captions.Values.Select(c => new { c.Id, c.Text, c.IsFinal }), parts
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }
}
