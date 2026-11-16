using System.IO;
using System.Text.Json;
using BlueJays.App.Services;
using BlueJays.Core.Audio;
using BlueJays.Core.Conversation;
using BlueJays.Core.Models;

internal static class SpeechEngineTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Legacy settings retain Whisper while new model paths and device options round-trip independently", () => Sync(() =>
        {
            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"ModelPath\":\"custom.bin\"}")!;
            Check(legacy.SpeechEngine == SpeechEngine.Whisper && legacy.ModelPath == "custom.bin", "Legacy engine changed.");
            var settings = legacy with { SpeechEngine = SpeechEngine.Qwen3Asr, SenseVoiceModelPath = "sense", QwenModelPath = "qwen",
                SpeechDevice = SpeechDevice.OpenVinoCpu, SpeechCpuThreads = 4, QwenLivePreviews = true, AllowSpeechCpuFallback = false };
            var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            Check(saved.SpeechEngine == settings.SpeechEngine && saved.SpeechDevice == settings.SpeechDevice &&
                saved.ModelPath == "custom.bin" && saved.SenseVoiceModelPath == "sense" && saved.QwenModelPath == "qwen" &&
                saved.SpeechCpuThreads == 4 && saved.QwenLivePreviews && !saved.AllowSpeechCpuFallback, "Model selections did not survive saving.");
        }));
        yield return ("Qwen final-only capture emits all speech without spending inference on previews", () => Sync(() =>
        {
            var settings = new AppSettings { SpeechEngine = SpeechEngine.Qwen3Asr };
            var buffer = new StreamingSpeechBuffer(enablePreviews: SpeechTuning.EnablePreviews(settings), minimumPreviewSeconds: SpeechTuning.MinimumPreviewSeconds(settings));
            var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
            var samples = Tone(16000 * 11); buffer.Push(samples); buffer.Flush();
            Check(windows.Count == 2 && windows.All(w => w.IsFinal), "Qwen emitted repeated drafts or lost a final chunk.");
            Check(windows.Sum(w => w.Samples.Length) >= samples.Length, "Final speech was discarded.");
            Check(SpeechTuning.EnablePreviews(settings with { QwenLivePreviews = true }), "Optional Qwen previews cannot be enabled.");
            Check(SpeechTuning.EnablePreviews(settings with { SpeechEngine = SpeechEngine.SenseVoice }), "SenseVoice previews were disabled.");
        }));
        yield return ("Cached inference acknowledgements preserve slow-decoder preview backoff", () => Sync(() =>
        {
            var buffer = new StreamingSpeechBuffer(maxSeconds: 20); var windows = new List<SpeechWindow>(); buffer.Updated += windows.Add;
            buffer.Push(Tone(32000)); var id = windows[^1].Id;
            buffer.Acknowledge(id, 0, 3); buffer.Acknowledge(id, 0, 0);
            var before = windows.Count; buffer.Push(Tone(32000));
            Check(windows.Count == before, "A zero-cost cache hit reset the adaptive interval."); buffer.Flush();
        }));
        yield return ("Final speech displaces previews at capacity while final overflow remains explicit", async () =>
        {
            var queue = new SpeechWorkQueue(1);
            CapturedWindow Capture(string id, bool final) => new(id, id, new(id, 1, DateTimeOffset.Now, [1], final, false));
            Check(queue.Put(Capture("draft", false)) && queue.Put(Capture("final", true)), "Final speech could not displace a preview.");
            Check(queue.Put(Capture("optional", false)), "An optional preview incorrectly reported audio loss.");
            Check(!queue.Put(Capture("overflow", true)), "Final overflow was hidden.");
            Check((await queue.TakeAsync(default))!.Window.Id == "final", "A draft displaced completed speech."); queue.Release("final"); queue.Complete();
        });
        yield return ("ONNX timestamps only permit acoustic cuts when exact token alignment is available", () => Sync(() =>
        {
            var coarse = OnnxSpeechParts.Create(new(true, "今日は晴れです。"), 5);
            Check(coarse.Count == 1 && coarse[0].EndSeconds == 5, "Untimed Qwen text got invented timestamps.");
            var exact = OnnxSpeechParts.Create(new(true, "Hello world", Tokens: ["Hello", "▁world"], Timestamps: [0, 1]), 3);
            Check(exact.Count == 2 && exact[0].EndSeconds == 1, "Valid CTC timestamps were discarded.");
            var itn = OnnxSpeechParts.Create(new(true, "50円", Tokens: ["五十", "円"], Timestamps: [0, 1]), 3);
            Check(itn.Count == 1 && itn[0].EndSeconds == 3, "ITN changed text but the audio was cut using unaligned tokens.");
            var invalid = OnnxSpeechParts.Create(new(true, "ab", Tokens: ["a", "b"], Timestamps: [1, 0]), 3);
            Check(invalid.Count == 1, "Nonmonotonic timestamps were accepted.");
        }));
        yield return ("ONNX model selection checks complete folders and rejects unsupported SenseVoice languages before native loading", () => Sync(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "BlueJays-model-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                var settings = new AppSettings { SpeechEngine = SpeechEngine.SenseVoice, SenseVoiceModelPath = folder };
                Check(!SpeechModels.IsReady(settings), "An empty folder was accepted.");
                foreach (var file in SpeechModels.SenseVoice.RequiredFiles) File.WriteAllText(Path.Combine(folder, file), "fixture");
                Check(SpeechModels.IsReady(settings) && !SpeechModels.IsReady(settings with { SpeechEngine = SpeechEngine.Qwen3Asr, QwenModelPath = folder }), "Engine-specific files were not checked.");
                SpeechModels.Validate(settings, new() { SpokenLanguages = ["ja", "en"] });
                try { SpeechModels.Validate(settings, new() { SpokenLanguages = ["fr"] }); throw new Exception("Unsupported language was accepted."); }
                catch (ArgumentException) { }
            }
            finally { Directory.Delete(folder, true); }
        }));
    }
    private static float[] Tone(int count) => Enumerable.Range(0, count).Select(i => (float)(.2 * Math.Sin(i * 2 * Math.PI * 440 / 16000))).ToArray();
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
