using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BlueJays.Core.Audio;
using BlueJays.Core.Models;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Services;

/// <summary>
/// One persistent multilingual model, one inference at a time. No Tiny LID pass or
/// per-language model copies. The host isolates native ORT/OpenVINO from Whisper,
/// and allows cancellation to terminate a stuck driver without blocking WPF.
/// </summary>
public sealed class OnnxSpeechRecognizer : IConversationSpeechRecognizer, IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly ConversationOptions conversation;
    private readonly string? hostExecutable;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Queue<string> errors = new();
    private readonly object errorGate = new();
    private Process? host;
    private Task stderr = Task.CompletedTask;
    private bool disposed;
    public int WorkerCount => 1;
    public bool UsesGpu { get; private set; }
    public string BackendName { get; private set; } = "";
    public string? StartupWarning { get; private set; }

    public OnnxSpeechRecognizer(AppSettings settings, ConversationOptions conversation, string? hostExecutable = null)
    {
        SpeechModels.Validate(settings, conversation);
        this.settings = settings; this.conversation = conversation;
        this.hostExecutable = hostExecutable;
    }

    public async Task WarmAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (host is not null) return;
            var device = settings.SpeechDevice;
            if (settings.SpeechEngine == SpeechEngine.Qwen3Asr && device == SpeechDevice.OpenVinoGpu)
            {
                if (!settings.AllowSpeechCpuFallback) throw new NotSupportedException(L.T("Qwen GPU is unavailable in this build. Select OpenVINO CPU or ONNX Runtime CPU."));
                device = SpeechDevice.OpenVinoCpu;
                StartupWarning = L.T("Qwen GPU is unavailable in this build. OpenVINO CPU is active.");
            }
            try { await StartAsync(device, token); }
            catch (Exception ex) when (settings.AllowSpeechCpuFallback && device != SpeechDevice.Cpu && ex is not OperationCanceledException)
            {
                StopHost();
                await StartAsync(SpeechDevice.Cpu, token);
                StartupWarning = L.F("OpenVINO could not start. CPU fallback is active. {0}", ex.Message);
            }
        }
        finally { gate.Release(); }
    }

    private async Task StartAsync(SpeechDevice device, CancellationToken token)
    {
        var executable = hostExecutable ?? Path.Combine(AppContext.BaseDirectory, "speech-host", "BlueJays.SpeechHost.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The speech-host folder is missing. Use the complete BlueJays package.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the speech host.");
        lock (errorGate) errors.Clear();
        var process = host;
        stderr = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    lock (errorGate) { errors.Enqueue(line.Length > 500 ? line[..500] : line); while (errors.Count > 32) errors.Dequeue(); }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var languages = conversation.SpokenLanguages.Where(l => l != "auto").Distinct().ToArray();
        SpeechHostResponse response;
        try
        {
            // The helper has its own working directory. Resolve staged relative
            // paths in the parent before crossing the process boundary.
            response = await SendAsync(new("load", settings.SpeechEngine, Path.GetFullPath(SpeechModels.PathFor(settings)), device,
                settings.SpeechCpuThreads, languages.Length == 1 ? languages[0] : "auto", conversation.Glossary), deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Speech model preparation exceeded three minutes."); }
        BackendName = response.Backend;
        UsesGpu = device == SpeechDevice.OpenVinoGpu;
    }

    public async Task<IReadOnlyList<RecognizedPart>> RecognizeAsync(CapturedWindow capture, CancellationToken token, string prompt = "")
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (host is null) throw new InvalidOperationException("The speech model is not loaded.");
            var samples = SpeechAudio.Prepare(capture.Window.Samples);
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            var response = await SendAsync(new("decode", Audio: Convert.ToBase64String(bytes)), token);
            var configured = conversation.SpokenLanguages.Where(l => l != "auto").Distinct().ToArray();
            if (string.IsNullOrEmpty(response.Language) && configured.Length == 1)
                response = response with { Language = configured[0] };
            return OnnxSpeechParts.Create(response, samples.Length / 16000d);
        }
        finally { gate.Release(); }
    }

    private async Task<SpeechHostResponse> SendAsync(SpeechHostRequest request, CancellationToken token)
    {
        var process = host ?? throw new InvalidOperationException("Speech host is unavailable.");
        // A native decode has no reliable managed cancellation callback. Terminate
        // only our own host process, leaving the meeting app and audio devices alone.
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                // Native libraries occasionally print diagnostics on stdout.
                if (!line.StartsWith('{')) continue;
                var response = JsonSerializer.Deserialize<SpeechHostResponse>(line) ?? throw new InvalidDataException("Invalid speech host response.");
                if (!response.Success)
                {
                    string failureDetail;
                    lock (errorGate) failureDetail = errors.LastOrDefault(e => e.Contains("Invalid value", StringComparison.OrdinalIgnoreCase) ||
                        e.Contains("not available", StringComparison.OrdinalIgnoreCase) || e.Contains("not supported", StringComparison.OrdinalIgnoreCase)) ?? "";
                    throw new InvalidOperationException((response.Error ?? "Speech recognition failed.") + (failureDetail.Length == 0 ? "" : " " + failureDetail));
                }
                return response;
            }
            token.ThrowIfCancellationRequested();
            await stderr.WaitAsync(TimeSpan.FromSeconds(2), token);
            string detail; lock (errorGate) detail = errors.LastOrDefault() ?? "Native speech host exited unexpectedly.";
            throw new InvalidOperationException(detail);
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is IOException or InvalidOperationException or ObjectDisposedException)
        { throw new OperationCanceledException(token); }
    }

    private void StopHost()
    {
        var process = host; host = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
    public Task ForgetAsync(string utteranceId) => Task.CompletedTask;
    public void Dispose() { if (disposed) return; disposed = true; StopHost(); /* An in-flight caller owns the semaphore until it exits. */ }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        var process = host;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new SpeechHostRequest("shutdown")).AsMemory(), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        finally { StopHost(); }
    }
}

public static class OnnxSpeechParts
{
    public static IReadOnlyList<RecognizedPart> Create(SpeechHostResponse response, double audioSeconds)
    {
        var text = response.Text.Trim();
        if (text.Length == 0) return [];
        // Qwen has no acoustic timestamps. ITN can also change SenseVoice's token
        // spelling. In either case keep a coarse whole-window boundary: estimating
        // audio cuts from character counts can permanently lose spoken words.
        var tokens = response.Tokens; var times = response.Timestamps;
        if (tokens is null || times is null || tokens.Length != times.Length || tokens.Length == 0)
            return [new(text, 0, audioSeconds, response.Language)];
        var cleaned = tokens.Select(t => t.Replace("▁", " ", StringComparison.Ordinal)).ToArray();
        if (string.Concat(cleaned).Trim() != text || times.Any(t => !float.IsFinite(t) || t < 0 || t > audioSeconds) ||
            times.Zip(times.Skip(1)).Any(pair => pair.First > pair.Second)) return [new(text, 0, audioSeconds, response.Language)];
        return cleaned.Select((t, i) => new RecognizedPart(t, times[i], i + 1 < times.Length ? times[i + 1] : audioSeconds, response.Language)).ToArray();
    }
}
