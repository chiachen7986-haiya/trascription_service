using BlueJays.Core.Models;

namespace BlueJays.App.Services;

/// <summary>Owns one explicitly selected endpoint, including startup and teardown.</summary>
public interface IAudioCaptureSource : IDisposable
{
    string SourceId { get; }
    event Action<CapturedWindow>? WindowReady;
    event Action<string, double>? LevelChanged;
    event Action<string>? Failed;
    Task StartAsync(CancellationToken token);
    void EnsureRunning();
    void Finish();
    void Acknowledge(string windowId, long sample, double decodeSeconds);
}
