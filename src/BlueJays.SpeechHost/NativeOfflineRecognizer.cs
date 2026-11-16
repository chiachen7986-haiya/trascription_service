using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SherpaOnnx;

/// <summary>
/// Uses sherpa's public C ABI and result parser, with checked SafeHandles. The
/// upstream facade accepts a null native recognizer and later reports a misleading
/// NullReferenceException when model/device initialization fails.
/// </summary>
internal sealed class NativeOfflineRecognizer : IDisposable
{
    internal sealed record Result(string Text, string[] Tokens, float[]? Timestamps, float[]? Durations, string Language);
    private readonly RecognizerHandle handle;
    public NativeOfflineRecognizer(OfflineRecognizerConfig config)
    {
        handle = new(NativeCreate(ref config));
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("Speech model initialization failed. Check the model files and selected compute device."); }
    }
    public Result Decode(float[] samples)
    {
        using var stream = new StreamHandle(CreateStream(handle));
        if (stream.IsInvalid) throw new InvalidOperationException("Could not create a speech stream.");
        AcceptWaveform(stream, 16000, samples, samples.Length); DecodeStream(handle, stream);
        var result = GetResult(stream);
        if (result == IntPtr.Zero) throw new InvalidOperationException("The speech engine returned no result.");
        try
        {
            var parsed = new OfflineRecognizerResult(result);
            // sherpa's managed facade omits lang. Copy this UTF-8 field while
            // the pinned 2.0.1 C result is alive; no extra LID inference is needed.
            var header = Marshal.PtrToStructure<ResultHeader>(result);
            return new(parsed.Text, parsed.Tokens, parsed.Timestamps, parsed.Durations, Marshal.PtrToStringUTF8(header.Language) ?? "");
        }
        finally { DestroyResult(result); }
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct ResultHeader
    {
        public IntPtr Text, Timestamps;
        public int Count;
        public IntPtr Tokens, TokensArray, Json, Language;
    }
    private sealed class RecognizerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RecognizerHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { DestroyRecognizer(handle); return true; }
    }
    private sealed class StreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public StreamHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { DestroyStream(handle); return true; }
    }
    private const string Library = "sherpa-onnx-c-api";
    [DllImport(Library, EntryPoint = "SherpaOnnxCreateOfflineRecognizer")] private static extern IntPtr NativeCreate(ref OfflineRecognizerConfig config);
    [DllImport(Library, EntryPoint = "SherpaOnnxDestroyOfflineRecognizer")] private static extern void DestroyRecognizer(IntPtr recognizer);
    [DllImport(Library, EntryPoint = "SherpaOnnxCreateOfflineStream")] private static extern IntPtr CreateStream(RecognizerHandle recognizer);
    [DllImport(Library, EntryPoint = "SherpaOnnxDestroyOfflineStream")] private static extern void DestroyStream(IntPtr stream);
    [DllImport(Library, EntryPoint = "SherpaOnnxAcceptWaveformOffline")] private static extern void AcceptWaveform(StreamHandle stream, int rate, float[] samples, int count);
    [DllImport(Library, EntryPoint = "SherpaOnnxDecodeOfflineStream")] private static extern void DecodeStream(RecognizerHandle recognizer, StreamHandle stream);
    [DllImport(Library, EntryPoint = "SherpaOnnxGetOfflineStreamResult")] private static extern IntPtr GetResult(StreamHandle stream);
    [DllImport(Library, EntryPoint = "SherpaOnnxDestroyOfflineRecognizerResult")] private static extern void DestroyResult(IntPtr result);
}
