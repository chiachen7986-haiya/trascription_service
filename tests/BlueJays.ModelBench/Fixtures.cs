using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Parquet;

internal static class Fixtures
{
    public static async Task<int> PrepareFleursAsync(string workspace)
    {
        var root = Path.Combine(workspace, "artifacts", "model-comparison");
        var output = Path.Combine(root, "audio"); Directory.CreateDirectory(output);
        using var file = File.OpenRead(Path.Combine(root, "downloads", "fleurs-ja-test.parquet"));
        await using var reader = await ParquetReader.CreateAsync(file);
        var fields = reader.Schema.GetDataFields();
        Console.WriteLine(string.Join(", ", fields.Select(f => f.Path.ToString())));
        using var group = reader.OpenRowGroupReader(0);
        var audio = new byte[]?[group.RowCount]; var references = new string?[group.RowCount]; var ids = new int?[group.RowCount];
        await group.ReadAsync(fields.Single(f => f.Path.ToString() == "audio/bytes" || f.Path.ToString() == "audio.bytes"), audio.AsMemory());
        await group.ReadAsync(fields.Single(f => f.Name == "raw_transcription"), references.AsMemory());
        await group.ReadAsync<int>(fields.Single(f => f.Name == "id"), ids.AsMemory());
        var clips = new List<Clip>();
        for (var i = 0; i < Math.Min(10, audio.Length); i++)
        {
            // First ten rows in the pinned public test shard, without selecting by outcome.
            var id = "fleurs-ja-" + i.ToString("D2");
            var target = Path.Combine(output, id + ".wav"); var bytes = audio[i] ?? throw new InvalidDataException("Missing FLEURS audio.");
            File.WriteAllBytes(target, bytes);
            clips.Add(new(id, "audio/" + id + ".wav", "ja", references[i] ?? throw new InvalidDataException("Missing reference."),
                "google/fleurs ja_jp test, revision 8d6448449ef85cacf10301f85c215be519e08061, row " + i + ", id " + ids[i] + ", CC-BY-4.0"));
        }
        File.WriteAllText(Path.Combine(root, "manifest-fleurs-ja.json"), JsonSerializer.Serialize(clips,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("Extracted the first ten FLEURS Japanese test recordings; attribution is in the manifest.");
        return 0;
    }

    public static int Prepare(string workspace)
    {
        var root = Path.Combine(workspace, "artifacts", "model-comparison");
        var source = Path.Combine(root, "models", "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25", "test_wavs");
        var output = Path.Combine(root, "audio"); Directory.CreateDirectory(output);
        var upstream = File.ReadAllLines(Path.Combine(source, "transcript.txt"));
        string Reference(string file) => upstream.Single(line => line.StartsWith(file + " ", StringComparison.Ordinal))[(file.Length + 1)..].Trim();
        var clips = new List<Clip>();
        Add("ja-lunch", Path.Combine(workspace, "artifacts", "test-data", "japanese.wav"), "ja",
            "うちの中学は弁当制で、持っていけない場合は、50円の学校販売のパンを買う。",
            "https://huggingface.co/spaces/k2-fsa/automatic-speech-recognition/tree/main/test_wavs/sense_voice", true);
        Add("ja-athlete", Path.Combine(source, "ja1.wav"), "ja", Reference("ja1.wav"), "sherpa-onnx Qwen3 test_wavs/transcript.txt", true);
        Add("en-jfk", Path.Combine(workspace, "artifacts", "test-data", "jfk.wav"), "en",
            "And so my fellow Americans ask not what your country can do for you ask what you can do for your country.",
            "https://github.com/ggml-org/whisper.cpp/tree/master/samples", true);
        Add("en-radio", Path.Combine(source, "f1_noise.wav"), "en", Reference("f1_noise.wav"), "sherpa-onnx Qwen3 test_wavs/transcript.txt", false);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(clips, json));
        File.WriteAllText(Path.Combine(root, "manifest-ja.json"), JsonSerializer.Serialize(clips.Where(c => c.Language == "ja"), json));
        File.WriteAllText(Path.Combine(root, "manifest-en.json"), JsonSerializer.Serialize(clips.Where(c => c.Language == "en"), json));
        File.WriteAllText(Path.Combine(root, "manifest-clean.json"), JsonSerializer.Serialize(clips.Where(c => !c.Id.EndsWith("-snr10", StringComparison.Ordinal)), json));
        File.WriteAllText(Path.Combine(root, "manifest-lunch.json"), JsonSerializer.Serialize(clips.Take(1), json));
        Console.WriteLine($"Prepared {clips.Count} conditions from four original clips. Noise variants are not independent recordings."); return 0;

        void Add(string id, string path, string language, string reference, string provenance, bool noisy)
        {
            using var wave = new WaveFileReader(path); ISampleProvider reader = wave.ToSampleProvider();
            if (reader.WaveFormat.Channels == 2) reader = new StereoToMonoSampleProvider(reader);
            if (reader.WaveFormat.Channels != 1) throw new ArgumentException("Use mono/stereo audio.");
            if (reader.WaveFormat.SampleRate != 16000) reader = new WdlResamplingSampleProvider(reader, 16000);
            var result = new List<float>(); var buffer = new float[16000]; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer[..count]);
            var samples = result.ToArray(); Save(id, samples, provenance);
            if (noisy)
            {
                // Deterministic white noise at 10 dB whole-clip RMS SNR. These are
                // controlled stress conditions, not realistic meeting recordings.
                var random = new Random(20261105); var noise = samples.Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
                var signalRms = Math.Sqrt(samples.Average(x => (double)x * x)); var noiseRms = Math.Sqrt(noise.Average(x => (double)x * x));
                var scale = signalRms / (Math.Sqrt(10) * noiseRms);
                var mixed = samples.Select((x, i) => (float)(x + noise[i] * scale)).ToArray();
                var peak = mixed.Max(x => Math.Abs(x)); if (peak > .99f) for (var i = 0; i < mixed.Length; i++) mixed[i] *= .99f / peak;
                Save(id + "-snr10", mixed, provenance + "; deterministic 10 dB white noise");
            }
            void Save(string name, float[] audio, string origin)
            {
                var target = Path.Combine(output, name + ".wav");
                using (var writer = new WaveFileWriter(target, new WaveFormat(16000, 16, 1))) writer.WriteSamples(audio, 0, audio.Length);
                clips.Add(new(name, "audio/" + name + ".wav", language, reference, origin));
                Console.WriteLine($"{name}: {audio.Length / 16000d:F2}s");
            }
        }
    }
}
