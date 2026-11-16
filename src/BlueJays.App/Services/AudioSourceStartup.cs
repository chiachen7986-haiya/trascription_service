using System.Runtime.InteropServices;
using BlueJays.App.Localization;
using BlueJays.Core.Models;

namespace BlueJays.App.Services;

public sealed class AudioDeviceUnavailableException(string reason) : Exception(reason)
{
    // Explicitly inactive endpoints cannot be fixed by retrying their old handle.
}

public sealed record AudioSourceIssue(AudioSourceSettings Source, Exception Error)
{
    public string Message
    {
        get
        {
            var reason = Error switch
            {
                AudioDeviceUnavailableException => Localizer.T(Error.Message),
                COMException when Error.HResult == unchecked((int)0x88890004) => Localizer.T("The selected audio device was disconnected or reconfigured."),
                COMException when Error.HResult == unchecked((int)0x80070005) => Localizer.T("Windows denied audio access. Check microphone privacy permissions."),
                COMException when Error.HResult == unchecked((int)0x80070490) => Localizer.T("The saved audio device no longer exists."),
                _ => Error.Message
            };
            return Localizer.F("Audio source “{0}”: {1} Reconnect it or use Choose device.", Source.Name, reason);
        }
    }
}

public sealed record AudioStartupResult(IReadOnlyList<IAudioCaptureSource> Captures, IReadOnlyList<AudioSourceIssue> Issues);

/// <summary>One unavailable device cannot prevent other explicitly selected sources from starting.</summary>
public static class AudioSourceStartup
{
    public static async Task<AudioStartupResult> StartAsync(IEnumerable<AudioSourceSettings> sources,
        Func<AudioSourceSettings, IAudioCaptureSource> create, Action<IAudioCaptureSource> configure, CancellationToken token)
    {
        var started = new List<IAudioCaptureSource>(); var issues = new List<AudioSourceIssue>();
        var selected = sources.Where(s => s.Enabled).ToArray();
        try
        {
            foreach (var source in selected)
            {
                for (var attempt = 0; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    IAudioCaptureSource? candidate = null;
                    try
                    {
                        candidate = create(source); configure(candidate);
                        await candidate.StartAsync(token);
                        started.Add(candidate); candidate = null; break;
                    }
                    catch (COMException ex) when (attempt == 0 && ex.HResult == unchecked((int)0x88890004))
                    {
                        // A reconfigured, still-active endpoint may work with new COM
                        // handles. Release the failed client before trying that same id once.
                        candidate?.Dispose(); candidate = null;
                        await Task.Delay(150, token);
                    }
                    catch (Exception ex) when (ex is COMException or AudioDeviceUnavailableException or InvalidOperationException or ArgumentException or NotSupportedException)
                    { issues.Add(new(source, ex)); break; }
                    finally { candidate?.Dispose(); }
                }
            }
            // Opening a Bluetooth microphone can reconfigure a previously opened
            // playback endpoint. Recheck the whole set before claiming it is listening.
            foreach (var capture in started.ToArray())
            {
                try { capture.EnsureRunning(); }
                catch (Exception ex) when (ex is COMException or AudioDeviceUnavailableException or InvalidOperationException)
                {
                    started.Remove(capture); capture.Dispose();
                    issues.Add(new(selected.First(s => s.Id == capture.SourceId), ex));
                }
            }
            return new(started, issues);
        }
        catch
        {
            // Cancellation/fatal startup failures must release sources already opened.
            foreach (var capture in started) capture.Dispose();
            throw;
        }
    }
}
