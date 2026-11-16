using System.Diagnostics;
using System.IO;
using System.Text;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.Core.Models;
using NAudio.Wave;

internal static class HistoryAndDenoisingTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Encrypted history restores raw captions, translations, selection and conversation context", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "Transcription-history-" + Guid.NewGuid().ToString("N"));
            try
            {
                var model = new MainViewModel(); var conversation = new ConversationViewModel(new() { Name = "計画", TargetLanguages = ["English"] });
                model.Conversations.Add(conversation); model.SelectedConversation = conversation;
                model.Add(new("line", "Alice", DateTimeOffset.Now, "経費精算を確認します。", "mic", 2, true, RawText: "経費生産を確認します。"));
                model.ApplyTranslation("line", 2, "English", "Review expense claims.", "Translated"); model.PriorityLanguage = "English";
                var store = new ConversationStore(directory); store.Save(ConversationStore.Snapshot(model));
                var bytes = File.ReadAllBytes(Path.Combine(directory, "conversations.v1.dat"));
                Check(!Encoding.UTF8.GetString(bytes).Contains("Review expense claims"), "History was not encrypted.");
                var restored = new MainViewModel(); var loaded = store.Load(); Check(loaded.Warning is null, "History load failed.");
                ConversationStore.Restore(restored, loaded.Archive!);
                Check(restored.SelectedConversation?.Id == conversation.Id && restored.PriorityLanguage == "English", "Selection/preferences were lost.");
                Check(restored.Captions.Single().HasCorrection && restored.Captions.Single().Translations.Single().Text == "Review expense claims.", "Raw/corrected text or translation was lost.");
                Check(restored.SelectedConversation!.Memory.Context(new HashSet<string>()).Contains("Alice"), "Resume context was not restored.");
                restored.Conversations.Clear(); restored.SelectedConversation = null; store.Save(ConversationStore.Snapshot(restored));
                Check(store.Load().Archive!.Conversations.Length == 0, "Clear-all did not persist.");
                File.WriteAllText(Path.Combine(directory, "conversations.v1.dat"), "broken");
                Check(store.Load().Warning is not null && File.ReadAllText(Path.Combine(directory, "conversations.v1.dat")) == "broken", "Corrupt history was overwritten.");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            return Task.CompletedTask;
        });
        yield return ("Floating language priority follows selection without changing transcript data", () =>
        {
            var model = new MainViewModel(); var first = new ConversationViewModel(new() { TargetLanguages = ["English", "Japanese"] });
            model.Conversations.Add(first); model.SelectedConversation = first;
            model.Add(new("a", "Mic", DateTimeOffset.Now, "原文")); model.ApplyTranslation("a", 1, "English", "Original", "Translated");
            model.PriorityLanguage = "English";
            Check(model.FloatingLines.Single(l => l.IsPriority).Text.Contains("Original"), "Wrong language enlarged.");
            model.PriorityLanguage = "";
            Check(model.FloatingLines.Single(l => l.IsPriority).Text.Contains("原文"), "Original cannot be prioritized.");
            model.SelectedConversation = new(new() { TargetLanguages = ["Japanese"] });
            Check(!model.FloatingLines.Any() && model.PriorityLanguage == "Japanese", "Priority leaked between conversations.");
            model.SelectedConversation = first; Check(model.PriorityLanguage == "", "Conversation preference was lost.");
            return Task.CompletedTask;
        });
    }
    public static int Denoise(string input)
    {
        using var reader = new WaveFileReader(input);
        Check(reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.Channels == 1, "Use 16 kHz mono fixtures.");
        var samples = new float[reader.SampleCount]; var count = reader.ToSampleProvider().Read(samples, 0, samples.Length);
        using var denoiser = new GtcrnDenoiser(); var result = new List<float>(); var clock = Stopwatch.StartNew();
        for (var at = 0; at < count; at += 800) result.AddRange(denoiser.Process(samples.AsSpan(at, Math.Min(800, count - at))));
        result.AddRange(denoiser.Flush());
        Check(result.Count > count - 1600 && result.Count < count + 1600 && result.All(float.IsFinite), "Denoising lost its tail or produced invalid samples.");
        var elapsed = clock.Elapsed.TotalSeconds; Check(elapsed < count / 16000d, "Denoiser could not keep up with realtime audio.");
        using var other = new GtcrnDenoiser(); var silence = other.Process(new float[3200]).Concat(other.Flush()).ToArray();
        Check(silence.All(float.IsFinite) && silence.All(s => Math.Abs(s) < .001), "State leaked into another microphone.");
        using var noiseFilter = new GtcrnDenoiser(); var random = new Random(42);
        var noise = Enumerable.Range(0, 16000 * 4).Select(i => (float)(.04 * Math.Sin(i * 2 * Math.PI * 120 / 16000) + .04 * (random.NextDouble() * 2 - 1))).ToArray();
        var filtered = new List<float>();
        for (var at = 0; at < noise.Length; at += 800) filtered.AddRange(noiseFilter.Process(noise.AsSpan(at, 800)));
        filtered.AddRange(noiseFilter.Flush());
        double Power(IEnumerable<float> values) => values.Select(v => (double)v * v).Average();
        var reduction = 10 * Math.Log10(Power(noise.Skip(16000)) / Math.Max(1e-15, Power(filtered.Skip(16000))));
        Check(reduction > 3, "Stationary microphone noise was not meaningfully reduced.");
        Console.WriteLine($"PASS synthetic hum/noise suppression: {reduction:F1} dB reduction.");
        Console.WriteLine($"PASS GTCRN: {count / 16000d:F2}s audio in {elapsed:F3}s; RTF {elapsed / (count / 16000d):F3}; {result.Count} output samples.");
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
