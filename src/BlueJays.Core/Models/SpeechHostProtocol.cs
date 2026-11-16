namespace BlueJays.Core.Models;

// A separate process isolates native libraries, driver faults and cancellation.
// PCM is 16 kHz mono float32. Only one request is in flight per model instance.
public sealed record SpeechHostRequest(string Command, SpeechEngine Engine = SpeechEngine.SenseVoice,
    string ModelDirectory = "", SpeechDevice Device = SpeechDevice.Cpu, int Threads = 6,
    string Language = "auto", string Glossary = "", string Audio = "");

public sealed record SpeechHostResponse(bool Success, string Text = "", string Backend = "", string? Error = null,
    string[]? Tokens = null, float[]? Timestamps = null, float[]? Durations = null,
    double DecodeSeconds = 0, int OpenVinoOptions = 0, string Language = "");
