using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Audio;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;
using NAudio.Wave;

internal static class StreamingSpeechBenchmark
{
    // Real wall-clock input through the production session, using public files.
    // Never captures, plays or saves audio and never calls a translation provider.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("--streaming-benchmark <model.bin> <ja.wav> <en.wav> [parallel] [cpu]");
        var ja = Read(args[2]); var en = Read(args[3]);
        var engine = args.Contains("sensevoice") ? SpeechEngine.SenseVoice : args.Contains("qwen") ? SpeechEngine.Qwen3Asr : SpeechEngine.Whisper;
        var settings = new AppSettings { ModelPath = args[1], SpeechEngine = engine, SenseVoiceModelPath = args[1], QwenModelPath = args[1],
            SpeechDevice = args.Contains("cpu") ? SpeechDevice.Cpu : args.Contains("openvino-cpu") ? SpeechDevice.OpenVinoCpu : SpeechDevice.OpenVinoGpu,
            AllowSpeechCpuFallback = false, UseGpuAcceleration = !args.Contains("cpu"),
            ParallelLanguageDecoding = args.Contains("parallel"), SpeechThreshold = .002,
            Sources = [new() { Id = "ja", Name = "Japanese sample" }, new() { Id = "en", Name = "English sample" }] };
        var durationArgument = args.FirstOrDefault(a => a.StartsWith("seconds=", StringComparison.Ordinal));
        var duration = durationArgument is not null && int.TryParse(durationArgument[8..], out var seconds) ? Math.Clamp(seconds, 30, 600) : 30;
        var captions = new ConcurrentDictionary<string, Transcript>(); var errors = new ConcurrentQueue<string>();
        var lag = new ConcurrentQueue<double>();
        await using var session = new InterpretationSession(settings, "", new() { SpokenLanguages = ["ja", "en"], TargetLanguages = [] }, new ConversationMemory(),
            createCapture: source => new SampleCapture(source, source.Id == "ja" ? ja : en, settings));
        session.Recognized += caption => captions[caption.Id] = caption;
        session.Failed += errors.Enqueue; session.SpeechLagChanged += lag.Enqueue;
        var clock = Stopwatch.StartNew(); await session.StartAsync(); var startup = clock.Elapsed.TotalSeconds;
        await Task.Delay(TimeSpan.FromSeconds(duration)); clock.Restart(); await session.FinishAsync();
        var orderedLag = lag.Order().ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { model = Path.GetFileName(args[1]), backend = session.BackendDescription,
            source_count = session.ActiveSourceCount, wall_clock_input_seconds = duration, startup_seconds = startup,
            finish_seconds = clock.Elapsed.TotalSeconds, completed_captions = captions.Count, decode_updates = lag.Count,
            maximum_lag_seconds = orderedLag.LastOrDefault(), p95_lag_seconds = orderedLag.Length == 0 ? 0 : orderedLag[(int)((orderedLag.Length - 1) * .95)],
            last_lag_seconds = lag.LastOrDefault(), all_final = captions.Values.All(c => c.IsFinal), errors = errors.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        if (errors.Count > 0 || captions.Count == 0 || captions.Values.Any(c => !c.IsFinal) ||
            !captions.Values.Any(c => c.SourceId == "ja" && c.Text.Contains("中学")) || !captions.Values.Any(c => c.SourceId == "en" && c.Text.Contains("Americans")))
            throw new Exception("Streaming production pipeline lost speech, routed a language incorrectly, or left an unfinished caption.");
        return 0;
    }

    private static float[] Read(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1) throw new ArgumentException("Use 16 kHz mono WAV.");
        var samples = new float[reader.SampleCount]; var count = reader.ToSampleProvider().Read(samples, 0, samples.Length);
        Array.Resize(ref samples, count); return samples;
    }

    private sealed class SampleCapture : IAudioCaptureSource
    {
        private readonly AudioSourceSettings source;
        private readonly float[] samples;
        private readonly StreamingSpeechBuffer buffer;
        private readonly CancellationTokenSource cancellation = new();
        private readonly object gate = new();
        private Task feeding = Task.CompletedTask;
        private readonly float[] silence = new float[320];
        private bool running;
        public string SourceId => source.Id;
        public event Action<CapturedWindow>? WindowReady;
        public event Action<string, double>? LevelChanged;
        public event Action<string>? Failed;

        public SampleCapture(AudioSourceSettings source, float[] samples, AppSettings settings)
        {
            this.source = source; this.samples = samples;
            buffer = new(settings.SpeechThreshold, settings.SilenceMilliseconds, settings.MaxPhraseSeconds,
                SpeechTuning.EnablePreviews(settings), SpeechTuning.MinimumPreviewSeconds(settings));
            buffer.Updated += window => WindowReady?.Invoke(new(source.Id, source.Name, window));
        }
        public Task StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); running = true;
            feeding = Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew(); var sent = 0;
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        var due = (int)(clock.Elapsed.TotalSeconds * 16000);
                        while (sent + 320 <= due)
                        {
                            var cursor = sent % (samples.Length + 22400);
                            lock (gate)
                            {
                                if (cursor < samples.Length)
                                {
                                    var count = Math.Min(320, samples.Length - cursor); buffer.Push(samples.AsSpan(cursor, count));
                                    if (count < 320) buffer.Push(silence.AsSpan(0, 320 - count));
                                }
                                else buffer.Push(silence);
                            }
                            sent += 320; LevelChanged?.Invoke(SourceId, 0);
                        }
                        await Task.Delay(10, cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex) { Failed?.Invoke(ex.Message); }
            });
            return Task.CompletedTask;
        }
        public void EnsureRunning() { if (!running) throw new InvalidOperationException("Sample input is stopped."); }
        public void Acknowledge(string id, long throughSample, double decodeSeconds) { lock (gate) buffer.Acknowledge(id, throughSample, decodeSeconds); }
        public void Finish() { Dispose(); lock (gate) buffer.Flush(); }
        public void Dispose() { if (!running) return; running = false; cancellation.Cancel(); feeding.GetAwaiter().GetResult(); }
    }
}
