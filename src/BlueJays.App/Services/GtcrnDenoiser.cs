using System.IO;
using System.Runtime.InteropServices;

namespace BlueJays.App.Services;

/// <summary>Stateful 16 kHz streaming GTCRN, one CPU thread per microphone. Never shared between sources.</summary>
public sealed class GtcrnDenoiser : IDisposable
{
    private static readonly Lazy<IntPtr> Library = new(() =>
    {
        var file = Path.Combine(AppContext.BaseDirectory, "speech-host", "sherpa-onnx-c-api.dll");
        // Resolve dependent ORT/LLVM libraries beside this pinned native bridge.
        var module = LoadLibraryEx(file, IntPtr.Zero, 0x100 | 0x1000);
        return module != IntPtr.Zero ? module : throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load the microphone denoiser runtime.");
    });
    [StructLayout(LayoutKind.Sequential)]
    private struct Config
    {
        public IntPtr Model;
        public int Threads, Debug;
        public IntPtr Provider, DpdfModel;
        public float Attenuation;
        public IntPtr Enroll, Chunk, Voice;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Audio { public IntPtr Samples; public int Count, Rate; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create(ref Config config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Run(IntPtr handle, float[] samples, int count, int rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FlushCall(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RateCall(IntPtr handle);
    private static T Api<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library.Value, name));
    private readonly Run run;
    private readonly FlushCall flush;
    private readonly Destroy destroy, freeAudio;
    private IntPtr handle;
    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", "gtcrn.onnx");

    public GtcrnDenoiser(string? model = null)
    {
        model ??= ModelPath;
        if (!File.Exists(model)) throw new FileNotFoundException("GTCRN model is missing. Restore models/gtcrn.onnx or disable microphone noise suppression.", model);
        var path = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(model)); var provider = Marshal.StringToCoTaskMemUTF8("cpu");
        run = Api<Run>("SherpaOnnxOnlineSpeechDenoiserRun"); flush = Api<FlushCall>("SherpaOnnxOnlineSpeechDenoiserFlush");
        destroy = Api<Destroy>("SherpaOnnxDestroyOnlineSpeechDenoiser"); freeAudio = Api<Destroy>("SherpaOnnxDestroyDenoisedAudio");
        try { var config = new Config { Model = path, Threads = 1, Provider = provider }; handle = Api<Create>("SherpaOnnxCreateOnlineSpeechDenoiser")(ref config); }
        finally { Marshal.FreeCoTaskMem(path); Marshal.FreeCoTaskMem(provider); }
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not initialize GTCRN microphone noise suppression.");
        if (Api<RateCall>("SherpaOnnxOnlineSpeechDenoiserGetSampleRate")(handle) != 16000) { Dispose(); throw new InvalidOperationException("GTCRN must use a 16 kHz model."); }
        // Compile kernels before WASAPI starts producing microphone packets.
        try { Process(new float[1600]); Flush(); Api<Destroy>("SherpaOnnxOnlineSpeechDenoiserReset")(handle); }
        catch { Dispose(); throw; }
    }
    public float[] Process(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        return Copy(run(handle, samples.ToArray(), samples.Length, 16000));
    }
    public float[] Flush() { ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this); return Copy(flush(handle)); }
    private float[] Copy(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        try
        {
            var audio = Marshal.PtrToStructure<Audio>(pointer);
            if (audio.Count < 0 || audio.Count > 16000 * 30 || audio.Rate != 16000) throw new InvalidDataException("Invalid denoiser output.");
            var result = new float[audio.Count]; if (audio.Count > 0) Marshal.Copy(audio.Samples, result, 0, audio.Count);
            return result;
        }
        finally { freeAudio(pointer); }
    }
    public void Dispose() { if (handle == IntPtr.Zero) return; destroy(handle); handle = IntPtr.Zero; }
    [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}
