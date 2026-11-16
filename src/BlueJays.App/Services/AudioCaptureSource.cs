using System.Diagnostics;
using System.Runtime.InteropServices;
using BlueJays.Core.Audio;
using BlueJays.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BlueJays.App.Services;

/// <summary>One independent WASAPI stream. Windows performs high-quality resampling to 16 kHz mono.</summary>
public sealed class AudioCaptureSource : IAudioCaptureSource
{
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly StreamingSpeechBuffer segmenter;
    private readonly GtcrnDenoiser? denoiser;
    private readonly AudioSourceSettings source;
    private readonly object gate = new();
    private readonly Stopwatch sincePacket = Stopwatch.StartNew();
    private readonly Timer gapTimer;
    private readonly float[] silence = new float[800];
    private bool disposed;
    private Exception? stoppedError;

    public event Action<CapturedWindow>? WindowReady;
    public event Action<string, double>? LevelChanged;
    public event Action<string>? Failed;
    public string SourceId => source.Id;

    public AudioCaptureSource(AudioSourceSettings source, AppSettings settings)
    {
        this.source = source;
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(source.DeviceId);
        WasapiCapture? opened = null;
        Timer? timer = null;
        try
        {
            // A saved Bluetooth endpoint can still be found by id while unplugged.
            // Check it before WASAPI activation; never substitute another device.
            if (device.State != DeviceState.Active)
                throw new AudioDeviceUnavailableException("The selected audio device is disconnected or disabled.");
            var flow = source.Kind == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
            if (device.DataFlow != flow)
                throw new AudioDeviceUnavailableException("The saved device has the wrong audio input or output type.");
            capture = opened = source.Kind == AudioSourceKind.ComputerAudio
                ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, false, 50);
            capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            if (source.Kind == AudioSourceKind.Microphone && settings.MicrophoneNoiseSuppression) denoiser = new();
            segmenter = new(settings.SpeechThreshold, settings.SilenceMilliseconds, settings.MaxPhraseSeconds,
                SpeechTuning.EnablePreviews(settings), SpeechTuning.MinimumPreviewSeconds(settings));
            segmenter.Updated += window => WindowReady?.Invoke(new(source.Id, source.Name, window));
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            // Some loopback devices stop delivering packets when playback becomes silent.
            // Advance the speech gate during those gaps so the last phrase still finalizes.
            gapTimer = timer = new Timer(InsertGapSilence, null, Timeout.Infinite, Timeout.Infinite);
        }
        catch
        {
            timer?.Dispose();
            denoiser?.Dispose();
            try { opened?.Dispose(); } finally { device.Dispose(); }
            throw;
        }
    }

    public void Start()
    {
        capture.StartRecording();
        gapTimer.Change(50, 50);
    }

    public async Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Start();
        var wait = Stopwatch.StartNew();
        // StartRecording initializes synchronously but IAudioClient.Start runs on
        // NAudio's capture thread. Do not report success before that has succeeded.
        while (capture.CaptureState == CaptureState.Starting && wait.ElapsedMilliseconds < 2000)
            await Task.Delay(10, token);
        token.ThrowIfCancellationRequested();
        EnsureRunning();
    }

    public void EnsureRunning()
    {
        if (capture.CaptureState != CaptureState.Capturing)
            throw Volatile.Read(ref stoppedError) ?? new AudioDeviceUnavailableException("The selected audio device could not start recording.");
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        lock (gate)
        {
            if (disposed) return;
            sincePacket.Restart();
            var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            double sum = 0;
            foreach (var sample in samples) sum += sample * sample;
            LevelChanged?.Invoke(source.Id, Math.Min(1, Math.Sqrt(sum / samples.Length) * 5));
            // Filtering precedes the speech gate so noise does not keep creating
            // speech windows. Loopback audio never passes through this denoiser.
            segmenter.Push(denoiser is null ? samples : denoiser.Process(samples));
        }
    }

    private void InsertGapSilence(object? state)
    {
        lock (gate)
        {
            if (disposed || sincePacket.ElapsedMilliseconds < 150) return;
            try { segmenter.Push(denoiser is null ? silence : denoiser.Process(silence)); }
            catch (Exception ex)
            {
                gapTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Failed?.Invoke("Microphone noise suppression failed: " + ex.Message); return;
            }
            LevelChanged?.Invoke(source.Id, 0);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (disposed || e.Exception is null) return;
            Volatile.Write(ref stoppedError, e.Exception);
            gapTimer.Change(Timeout.Infinite, Timeout.Infinite);
            segmenter.Flush();
        }
        Failed?.Invoke(new AudioSourceIssue(source, e.Exception).Message);
    }

    public void Dispose()
    {
        Exception? filterError = null;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            try { if (denoiser is not null) segmenter.Push(denoiser.Flush()); }
            catch (Exception ex) { filterError = ex; }
            finally { denoiser?.Dispose(); }
        }
        gapTimer.Dispose();
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        // Never join the capture thread while holding gate: its final callback may need it.
        try { capture.Dispose(); } finally { device.Dispose(); }
        if (filterError is not null) Failed?.Invoke("Could not finish microphone noise suppression: " + filterError.Message);
    }

    public void Finish()
    {
        Dispose();
        lock (gate) segmenter.Flush();
    }

    public void Acknowledge(string windowId, long sample, double decodeSeconds)
    { lock (gate) if (!disposed) segmenter.Acknowledge(windowId, sample, decodeSeconds); }
}
