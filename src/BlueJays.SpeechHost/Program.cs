using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BlueJays.Core.Models;
using SherpaOnnx;

// Never capture audio, read user settings, or contact a network from this host.
// The parent owns scheduling and can terminate an unresponsive native decoder.
Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
NativeOfflineRecognizer? recognizer = null;
string backend = "";
try
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        SpeechHostResponse response;
        try
        {
            if (line.Length > 4_000_000) throw new InvalidDataException("Speech request is too large.");
            var request = JsonSerializer.Deserialize<SpeechHostRequest>(line) ?? throw new InvalidDataException("Empty speech request.");
            if (request.Command == "shutdown") break;
            if (request.Command == "load")
            {
                if (recognizer is not null) throw new InvalidOperationException("A model is already loaded.");
                if (request.Engine == SpeechEngine.Qwen3Asr && request.Device == SpeechDevice.OpenVinoGpu)
                    throw new NotSupportedException("Qwen GPU is unavailable in this build. Use OpenVINO CPU or ONNX Runtime CPU.");
                var config = new OfflineRecognizerConfig();
                config.ModelConfig.NumThreads = Math.Clamp(request.Threads, 1, Math.Min(12, Environment.ProcessorCount));
                config.ModelConfig.Provider = "cpu";
                if (request.Device != SpeechDevice.Cpu)
                {
                    // The patched native build refuses silent EP fallback. The
                    // parent explicitly restarts on CPU when fallback is allowed.
                    _ = Native.OpenVinoOptions();
                    var device = request.Device == SpeechDevice.OpenVinoGpu ? "GPU" : "CPU";
                    var file = Path.Combine(Path.GetTempPath(), "BlueJays-OpenVINO-" + Environment.ProcessId + ".config");
                    var profile = Environment.GetEnvironmentVariable("BLUEJAYS_STT_PROFILE_PREFIX");
                    var cache = Environment.GetEnvironmentVariable("BLUEJAYS_STT_CACHE_DIR") ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueJays", "openvino-cache");
                    Directory.CreateDirectory(cache);
                    var tuning = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        [device] = device == "CPU"
                            ? new Dictionary<string, object> { ["PERFORMANCE_HINT"] = "LATENCY", ["NUM_STREAMS"] = "1", ["INFERENCE_NUM_THREADS"] = config.ModelConfig.NumThreads.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                            : new Dictionary<string, object> { ["PERFORMANCE_HINT"] = "LATENCY", ["NUM_STREAMS"] = "1" }
                    });
                    // The default FP16 GPU path returned empty text for this
                    // export on the tested Iris Xe. Preserve FP32 activations.
                    File.WriteAllText(file, "device_type=" + device + "\nprecision=FP32\ncache_dir=" + cache.Replace('\\', '/') + "\nload_config=" + tuning + "\n" +
                        (string.IsNullOrEmpty(profile) ? "" : "ProfilingFilePrefix=" + profile.Replace('\\', '/') + "\n"));
                    config.ModelConfig.Provider = "openvino:" + file;
                }
                if (request.Engine == SpeechEngine.SenseVoice)
                {
                    config.ModelConfig.SenseVoice.Model = Path.Combine(request.ModelDirectory, "model.int8.onnx");
                    config.ModelConfig.SenseVoice.Language = request.Language;
                    config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                    config.ModelConfig.Tokens = Path.Combine(request.ModelDirectory, "tokens.txt");
                }
                else if (request.Engine == SpeechEngine.Qwen3Asr)
                {
                    config.ModelConfig.Qwen3Asr.ConvFrontend = Path.Combine(request.ModelDirectory, "conv_frontend.onnx");
                    config.ModelConfig.Qwen3Asr.Encoder = Path.Combine(request.ModelDirectory, "encoder.int8.onnx");
                    config.ModelConfig.Qwen3Asr.Decoder = Path.Combine(request.ModelDirectory, "decoder.int8.onnx");
                    config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(request.ModelDirectory, "tokenizer");
                    // Audio windows are at most 20 s. Bound pathological generation
                    // without changing the conversation or running a second LID model.
                    config.ModelConfig.Qwen3Asr.MaxNewTokens = 256;
                    config.ModelConfig.Qwen3Asr.Hotwords = request.Glossary;
                }
                else throw new ArgumentException("Unsupported host speech engine.");
                recognizer = new(config);
                backend = (request.Engine == SpeechEngine.SenseVoice ? "SenseVoice" : "Qwen3-ASR") + " · " + (request.Device switch
                {
                    SpeechDevice.OpenVinoGpu => "OpenVINO GPU / CPU",
                    SpeechDevice.OpenVinoCpu => "OpenVINO CPU",
                    _ => "CPU"
                });
                // Compile and warm kernels before WASAPI starts accumulating audio.
                Decode(new float[16000]);
                var count = request.Device == SpeechDevice.Cpu ? 0 : Native.OpenVinoOptions();
                if (request.Device != SpeechDevice.Cpu && count == 0) throw new InvalidOperationException("OpenVINO was not activated.");
                response = new(true, Backend: backend, OpenVinoOptions: count);
            }
            else if (request.Command == "decode")
            {
                if (recognizer is null) throw new InvalidOperationException("Load a model first.");
                var bytes = Convert.FromBase64String(request.Audio);
                if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0 || bytes.Length > 16000 * 25 * sizeof(float))
                    throw new InvalidDataException("Expected up to 25 seconds of float32 PCM.");
                var samples = new float[bytes.Length / sizeof(float)]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                if (samples.Any(v => !float.IsFinite(v))) throw new InvalidDataException("PCM contains non-finite samples.");
                var clock = Stopwatch.StartNew(); var result = Decode(samples);
                response = new(true, result.Text, backend, Tokens: result.Tokens, Timestamps: result.Timestamps,
                    Durations: result.Durations, DecodeSeconds: clock.Elapsed.TotalSeconds, Language: result.Language);
            }
            else throw new ArgumentException("Unknown speech command.");
        }
        catch (Exception ex) { response = new(false, Error: ex.Message); }
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
        await Console.Out.FlushAsync();
    }
}
finally
{
    recognizer?.Dispose();
    var file = Path.Combine(Path.GetTempPath(), "BlueJays-OpenVINO-" + Environment.ProcessId + ".config");
    try { File.Delete(file); } catch (IOException) { }
}

NativeOfflineRecognizer.Result Decode(float[] samples)
{
    return recognizer!.Decode(samples);
}

internal static class Native
{
    [DllImport("sherpa-onnx-c-api", EntryPoint = "BlueJaysGetOpenVinoOptionCount")]
    internal static extern int OpenVinoOptions();
}
