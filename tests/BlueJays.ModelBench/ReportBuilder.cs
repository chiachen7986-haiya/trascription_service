using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class ReportBuilder
{
    public static int Build(string workspace)
    {
        var root = Path.Combine(workspace, "artifacts", "model-comparison");
        var results = Path.Combine(root, "results"); var report = new StringBuilder();
        var configurations = new[] { ("sensevoice-cpu", "SenseVoice Small INT8 · CPU"), ("qwen-cpu", "Qwen3-ASR 0.6B INT8 · CPU"),
            ("whisper-small-vulkan", "Whisper Small Q5_1 · Vulkan, fixed language"), ("whisper-routed-vulkan", "Whisper Small Q5_1 · Vulkan + Tiny routing"),
            ("whisper-base-vulkan", "Whisper Base Q5_1 · Vulkan, fixed language") };
        report.AppendLine("# Local STT comparison · 2026-11-05").AppendLine();
        report.AppendLine("Measured on this Windows 11 laptop: i5-1235U (2 performance / 8 efficient cores, 12 threads), Iris Xe 80-EU GPU, approximately 16 GB RAM. No API, context correction, live microphone or playback was used. Application preferences and the published application were not changed.").AppendLine();
        report.AppendLine("## Japanese read-speech comparison").AppendLine();
        report.AppendLine("First ten FLEURS Japanese test rows: **110.04 seconds, 443 normalized reference characters**. Same clips and references for every model. Each clip has three warm timed decodes after a first decode; per-clip warm medians are used. Corpus CER is total edit errors / total reference characters. This small sample is a screening test, not a meeting-accuracy benchmark.").AppendLine();
        report.AppendLine("| Configuration | Japanese CER | Warm compute / audio (RTF) | Mean warm decode per clip | 7.2 s Japanese demo | Peak process RAM (FLEURS) |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (prefix, label) in configurations)
        {
            using var corpus = Load(prefix + "-fleurs.json"); using var pilot = Load(prefix + "-pilot.json");
            var clips = corpus.RootElement.GetProperty("clips").EnumerateArray().ToArray();
            var audio = clips.Sum(c => Number(c, "audio_seconds")); var time = clips.Sum(c => Number(c, "median_seconds"));
            var errors = clips.Sum(c => Number(c, "character_errors")); var characters = clips.Sum(c => Number(c, "reference_characters"));
            if (clips.Length != 10 || Math.Abs(audio - 110.04) > .001 || characters != 443) throw new InvalidDataException("Mismatched FLEURS test set.");
            var lunch = pilot.RootElement.GetProperty("clips").EnumerateArray().Single(c => c.GetProperty("Id").GetString() == "ja-lunch");
            report.AppendLine($"| {label} | {F(errors / characters * 100, "F1")}% ({errors}/{characters}) | {F(time / audio, "F3")} | {F(time / clips.Length, "F2")} s | {F(Number(lunch, "median_seconds"), "F2")} s | {F(clips.Max(c => Number(c, "peak_working_set_bytes")) / 1048576, "F0")} MiB |");
        }
        report.AppendLine().AppendLine("RTF below 1 means faster than real time for one full-file decode. Repeated previews and concurrent sources add work. Process RAM excludes a reliable accounting of shared GPU allocations, so it is not a total-memory comparison. Fixed-language Whisper has prior language knowledge; SenseVoice/Qwen detect language internally, while routed Whisper includes Tiny classification among English/Japanese.").AppendLine();
        report.AppendLine("## English and noise checks").AppendLine();
        report.AppendLine("WER on the 11-second JFK excerpt, its deterministic 10 dB white-noise variant, and one 19.02-second noisy radio dialogue. These are three conditions from two recordings, not a broad English benchmark.").AppendLine();
        report.AppendLine("Whisper's radio output includes literal BEEP event markers, counted as word insertions by the stated tokenizer. The score therefore includes sound-event captions as well as spoken-word errors.").AppendLine();
        report.AppendLine("| Configuration | JFK WER | JFK + noise WER | Noisy radio WER | Radio warm decode |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var (prefix, label) in configurations)
        {
            using var pilot = Load(prefix + "-pilot.json"); var clips = pilot.RootElement.GetProperty("clips").EnumerateArray().ToArray();
            JsonElement Clip(string id) => clips.Single(c => c.GetProperty("Id").GetString() == id);
            report.AppendLine($"| {label} | {F(Number(Clip("en-jfk"), "word_error_rate") * 100, "F1")}% | {F(Number(Clip("en-jfk-snr10"), "word_error_rate") * 100, "F1")}% | {F(Number(Clip("en-radio"), "word_error_rate") * 100, "F1")}% | {F(Number(Clip("en-radio"), "median_seconds"), "F2")} s |");
        }
        var fixedEnglish = Path.Combine(results, "sensevoice-fixed-en.json");
        var fixedJapanese = Path.Combine(results, "sensevoice-fixed-ja-fleurs.json");
        if (File.Exists(fixedEnglish) && File.Exists(fixedJapanese))
        {
            using var en = JsonDocument.Parse(File.ReadAllText(fixedEnglish)); using var ja = JsonDocument.Parse(File.ReadAllText(fixedJapanese));
            var radio = en.RootElement.GetProperty("clips").EnumerateArray().Single(c => c.GetProperty("Id").GetString() == "en-radio");
            var clips = ja.RootElement.GetProperty("clips").EnumerateArray().ToArray();
            var errors = clips.Sum(c => Number(c, "character_errors")); var characters = clips.Sum(c => Number(c, "reference_characters"));
            report.AppendLine().AppendLine($"Forcing SenseVoice to **English** improved the radio WER to {F(Number(radio, "word_error_rate") * 100, "F1")}%, still behind Whisper Small and Qwen on this recording. Forcing **Japanese** gave CER {F(errors / characters * 100, "F1")}% ({errors}/{characters}) on FLEURS. Language forcing alone did not remove the observed English tradeoff.");
        }
        report.AppendLine().AppendLine("The Japanese demo's correct reference spelling 弁当制 was produced by SenseVoice. Qwen used that spelling too, but omitted a character elsewhere. The full raw hypotheses and errors are retained in the JSON results. No glossary, context prompt or API correction was applied.").AppendLine();
        report.AppendLine("## CPU thread-count check").AppendLine();
        report.AppendLine("Separate short runs on the same 7.2-second Japanese demo. These tune one clip only and do not replace the six-thread full-set comparison above.").AppendLine();
        report.AppendLine("| Threads | SenseVoice warm median | Qwen warm median |").AppendLine("| ---: | ---: | ---: |");
        foreach (var count in new[] { 1, 2, 4, 6 })
        {
            using var sense = Load($"sensevoice-cpu-threads-{count}.json"); using var qwen = Load($"qwen-cpu-threads-{count}.json");
            report.AppendLine($"| {count} | {F(Number(sense.RootElement.GetProperty("clips")[0], "median_seconds"), "F3")} s | {F(Number(qwen.RootElement.GetProperty("clips")[0], "median_seconds"), "F3")} s |");
        }
        var confirmation = Path.Combine(results, "qwen-cpu2-fleurs.json");
        if (File.Exists(confirmation))
        {
            using var twoThreads = JsonDocument.Parse(File.ReadAllText(confirmation));
            var clips = twoThreads.RootElement.GetProperty("clips").EnumerateArray().ToArray();
            var time = clips.Sum(c => Number(c, "median_seconds")); var audio = clips.Sum(c => Number(c, "audio_seconds"));
            var errors = clips.Sum(c => Number(c, "character_errors")); var characters = clips.Sum(c => Number(c, "reference_characters"));
            report.AppendLine().AppendLine($"A full ten-clip confirmation with **two Qwen threads** took {F(time, "F2")} s of warm compute for {F(audio, "F2")} s of audio (RTF **{F(time / audio, "F3")}**), with CER {F(errors / characters * 100, "F1")}% ({errors}/{characters}). The short-clip thread-count improvement did not establish a universal faster configuration. Measurements were sequential rather than randomized/interleaved; OS scheduling, power and thermals were not controlled.");
        }
        report.AppendLine().AppendLine("## Hardware acceleration").AppendLine();
        report.AppendLine("Whisper measurements above used actual Vulkan offload. The official sherpa-onnx 2.0.1 Windows runtime used for SenseVoice and Qwen exposes only CPUExecutionProvider. A real request for OpenVINO/GPU logged explicit CPU fallback; it was excluded from GPU timing comparisons. Intel's separate OpenVINO plugin 1.8.1, with ONNX Runtime 1.31.1, successfully enumerated this laptop's Iris Xe GPU and CPU, with no NPU device exposed. That establishes device availability, not accelerated transcription for either ONNX export. End-to-end SenseVoice/Qwen GPU latency remains unmeasured. INT8 precision alone does not enable Vulkan, OpenVINO or DirectML.").AppendLine();
        report.AppendLine("## Interpretation").AppendLine();
        report.AppendLine("SenseVoice is the strongest low-latency CPU candidate in these tests, particularly for Japanese-focused use. Qwen's Japanese CER advantage over SenseVoice is only four edits on this sample, while its English radio result is substantially better. Qwen needs more decode time and memory; the current repeated-preview strategy still needs throughput validation before adopting it. Retain an English-capable fallback if testing SenseVoice in mixed meetings. These results do not establish accuracy for company vocabulary, overlapping speakers, distant microphones or hour-long meetings.").AppendLine();
        report.AppendLine("SenseVoice's model weights use the separate custom FunASR model license; Qwen3-ASR weights are Apache-2.0. Runtime/source-code licenses do not automatically cover model weights. Model/dataset sources, hashes, methods, attribution, commands and hardware-probe limitations are documented in [README.md](README.md).").AppendLine();
        report.AppendLine("Raw per-clip JSON and the flattened metrics CSV are in `artifacts/model-comparison/`. No new model has been installed into the app's model selector.");
        File.WriteAllText(Path.Combine(workspace, "tests", "BlueJays.ModelBench", "RESULTS.md"), report.ToString(), new UTF8Encoding(false));
        var csv = new StringBuilder("result,engine,backend,clip,language,audio_seconds,median_seconds,rtf,character_errors,reference_characters,word_error_rate,peak_working_set_bytes\n");
        foreach (var path in Directory.GetFiles(results, "*.json").Order())
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path)); var data = json.RootElement;
            foreach (var clip in data.GetProperty("clips").EnumerateArray())
                csv.AppendLine(string.Join(',', new[] { Quote(Path.GetFileName(path)), Quote(data.GetProperty("engine").GetString()!), Quote(data.GetProperty("Backend").GetString()!),
                    Quote(clip.GetProperty("Id").GetString()!), Quote(clip.GetProperty("Language").GetString()!) }.Concat(
                    new[] { "audio_seconds", "median_seconds", "real_time_factor", "character_errors", "reference_characters", "word_error_rate", "peak_working_set_bytes" }.Select(k => clip.GetProperty(k).ToString()))));
        }
        File.WriteAllText(Path.Combine(root, "metrics.csv"), csv.ToString(), new UTF8Encoding(false));
        Console.WriteLine("Saved comparison report and metrics.csv."); return 0;

        JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(results, name)));
    }

    private static double Number(JsonElement element, string name) => element.GetProperty(name).GetDouble();
    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
