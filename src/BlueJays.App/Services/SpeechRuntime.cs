using System.Collections.Concurrent;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace BlueJays.App.Services;

/// <summary>Selects the bundled Vulkan runtime, with the portable CPU runtime as fallback.</summary>
public static class SpeechRuntime
{
    private static readonly object Gate = new();
    private static bool configured;

    public static void Configure()
    {
        lock (Gate)
        {
            if (configured) return;
            // Native library selection is process-wide. GPU use is then selected per model.
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
            configured = true;
        }
    }

    public static (WhisperFactory Factory, bool UsesGpu) Load(string modelPath, bool useGpu)
    {
        lock (Gate)
        {
            Configure();
            var messages = new ConcurrentQueue<string>();
            using var logger = LogProvider.AddLogger((_, message) => { if (message is not null) messages.Enqueue(message); });
            WhisperFactory factory;
            try { factory = WhisperFactory.FromPath(modelPath, new() { UseGpu = useGpu }); }
            catch (WhisperModelLoadException) when (useGpu)
            {
                // A missing/unsupported GPU must not prevent local recognition.
                factory = WhisperFactory.FromPath(modelPath, new() { UseGpu = false });
                return (factory, false);
            }
            var accelerated = useGpu && messages.Any(message =>
                message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) && message.Contains("total size", StringComparison.Ordinal));
            return (factory, accelerated);
        }
    }
}
