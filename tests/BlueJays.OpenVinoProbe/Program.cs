using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Intel.ML.OnnxRuntime.EP.OpenVINO;
using Microsoft.ML.OnnxRuntime;

// Device discovery and optional model-compilation probe, isolated from sherpa's
// CPU-only runtime. Compilation does not establish end-to-end ASR acceleration.
var env = OrtEnv.Instance(); var registration = "bluejays-openvino-probe";
// RID-specific .NET output flattens native assets beside the executable.
var flatLibrary = Path.Combine(AppContext.BaseDirectory, "onnxruntime_providers_openvino_plugin.dll");
env.RegisterExecutionProviderLibrary(registration, File.Exists(flatLibrary) ? flatLibrary : OpenVINOEp.GetLibraryPath());
try
{
    var devices = env.GetEpDevices().Where(d => d.EpName == OpenVINOEp.GetEpName()).ToArray();
    var info = devices.Select(d => new { provider = d.EpName, type = d.HardwareDevice.Type.ToString(), metadata = d.EpMetadata, hardware = d.HardwareDevice.Metadata }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { runtime = env.GetVersionString(), devices = info }, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Length > 0)
    {
        var gpu = devices.FirstOrDefault(d => d.HardwareDevice.Type.ToString().Contains("GPU", StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("No OpenVINO GPU device.");
        using var options = new SessionOptions { IntraOpNumThreads = 6, GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        options.AppendExecutionProvider(env, [gpu], new Dictionary<string, string>());
        var clock = Stopwatch.StartNew(); using var session = new InferenceSession(Path.GetFullPath(args[0]), options);
        Console.WriteLine(JsonSerializer.Serialize(new { model = Path.GetFileName(args[0]), session_created_seconds = clock.Elapsed.TotalSeconds,
            inputs = session.InputMetadata.ToDictionary(p => p.Key, p => new { type = p.Value.ElementType.Name, dimensions = p.Value.Dimensions }),
            note = "Compilation only; no actual speech inference, timing or provider partitioning was validated." }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
finally { env.UnregisterExecutionProviderLibrary(registration); }
